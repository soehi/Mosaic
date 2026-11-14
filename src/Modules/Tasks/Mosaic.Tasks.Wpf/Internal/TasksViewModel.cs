using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TasksViewModel : ObservableObject, IPageController
{
    private readonly ITasksApi _api;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserDialogService _dialogs;
    private readonly IDiagnosticSink _diagnostics;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _query;
    private long _queryVersion;
    private bool _busy;
    private string _busyFeedback = "";
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _loading;
    private bool _disposed;
    private int _filter;
    private TaskListItemViewModel? _selectedItem;
    private bool _editorOpen;
    private string _listError = "";
    private string _message = "";
    private string _error = "";
    private bool _conflict;

    internal TasksViewModel(ITasksApi api, IUiDispatcher dispatcher, IUserDialogService dialogs, IDiagnosticSink diagnostics)
    {
        _api = api;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        LoadCommand = new AsyncRelayCommand(LoadAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NewCommand = new AsyncRelayCommand(NewAsync, () => !IsBusy);
        SelectCommand = new AsyncRelayCommand<TaskItemDto>(SelectFromCommandAsync, _ => !IsBusy);
        BackToListCommand = new AsyncRelayCommand(BackToListAsync, () => !IsBusy && IsEditorOpen);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && Editor.IsDirty);
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => !IsBusy && Editor.HasTask);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !IsBusy && Editor.HasTask);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => !IsBusy && Editor.HasTask);
        Editor.PropertyChanged += OnEditorPropertyChanged;
        Items.CollectionChanged += (_, _) => NotifyListState();
    }

    public ObservableCollection<TaskListItemViewModel> Items { get; } = [];
    public TaskEditorViewModel Editor { get; } = new();
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand NewCommand { get; }
    public IAsyncRelayCommand<TaskItemDto> SelectCommand { get; }
    public IAsyncRelayCommand BackToListCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand ToggleCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }

    public TaskListItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            if (value is null)
            {
                SetSelectedItem(null);
                return;
            }
            _ = SelectFromListAsync(value);
        }
    }

    public bool IsEditorOpen
    {
        get => _editorOpen;
        private set
        {
            if (!SetProperty(ref _editorOpen, value)) return;
            OnPropertyChanged(nameof(ShowListMessage));
            BackToListCommand.NotifyCanExecuteChanged();
        }
    }

    public int FilterIndex
    {
        get => _filter;
        set => SetFilter(value, load: true);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            if (value) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SetProperty(ref _busy, value);
            if (!value) _idle.TrySetResult();
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(BusyFeedback));
            NotifyCommands();
        }
    }

    public string BusyFeedback { get => _busyFeedback; private set => SetProperty(ref _busyFeedback, value); }
    public bool CanEdit => !IsBusy;

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (!SetProperty(ref _loading, value)) return;
            NotifyListState();
        }
    }

    public bool IsListEmpty => !IsLoading && !HasListError && Items.Count == 0;
    public bool IsEmpty => IsListEmpty;
    public bool HasListError => !string.IsNullOrEmpty(ListError);
    public bool CanRefresh => !IsLoading && !IsBusy;
    public bool CanRetry => HasListError && CanRefresh;
    public bool HasGeneralError => !string.IsNullOrEmpty(Error) && !HasConflict;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool ShowListMessage => HasMessage && !IsEditorOpen;
    public string CountLabel => FilterIndex switch
    {
        1 => $"{Items.Count} 项未完成",
        2 => $"{Items.Count} 项已完成",
        _ => $"{Items.Count} 项任务"
    };
    public string EmptyTitle => FilterIndex switch
    {
        1 => "没有未完成任务",
        2 => "还没有已完成的任务",
        _ => "这里还没有任务"
    };
    public string EmptyDescription => FilterIndex switch
    {
        1 => "新建任务或恢复一项任务后，会显示在这里。",
        2 => "完成一项任务后，会显示在这里。",
        _ => "新建一项任务，给今天一个开始。"
    };
    public string ListError
    {
        get => _listError;
        private set
        {
            if (!SetProperty(ref _listError, value)) return;
            NotifyListState();
        }
    }
    public string Message
    {
        get => _message;
        private set
        {
            if (!SetProperty(ref _message, value)) return;
            OnPropertyChanged(nameof(HasMessage));
            OnPropertyChanged(nameof(ShowListMessage));
        }
    }
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasGeneralError));
        }
    }
    public bool HasConflict
    {
        get => _conflict;
        private set
        {
            if (SetProperty(ref _conflict, value)) OnPropertyChanged(nameof(HasGeneralError));
        }
    }

    public Task InitializeAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        if (_disposed) return;
        var version = ++_queryVersion;
        _query?.Cancel();
        var query = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _query = query;
        ListError = "";
        IsLoading = true;
        try
        {
            var result = await _api.ListAsync(new((TaskFilter)FilterIndex), query.Token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || version != _queryVersion) return;
                ApplyListResult(result);
            });
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var correlationId = Report(ex);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || version != _queryVersion) return;
                ClearList();
                ListError = $"无法加载任务，请刷新后重试。（诊断编号 {correlationId}）";
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (version != _queryVersion) return;
                IsLoading = false;
                _query = null;
            });
            query.Dispose();
        }
    }

    private async Task NewAsync()
    {
        if (!await CanLeaveAsync()) return;
        if (FilterIndex != 0) SetFilter(0, load: false);
        Editor.Load(null);
        SetSelectedItem(null);
        IsEditorOpen = true;
        ClearFeedback();
        await LoadAsync();
    }

    private async Task SelectFromCommandAsync(TaskItemDto? task)
    {
        if (task is null) return;
        if (task.Id == Editor.Current?.Id)
        {
            IsEditorOpen = true;
            return;
        }
        var previous = SelectedItem;
        var requested = Items.FirstOrDefault(item => item.Id == task.Id);
        if (requested is not null) SetSelectedItem(requested);
        if (!await SelectAsync(task)) SetSelectedItem(previous);
    }

    private async Task SelectFromListAsync(TaskListItemViewModel requested)
    {
        var previous = SelectedItem;
        SetSelectedItem(requested);
        if (requested.Id == Editor.Current?.Id)
        {
            IsEditorOpen = true;
            return;
        }
        if (!await SelectAsync(requested.Item)) SetSelectedItem(previous);
    }

    private async Task<bool> SelectAsync(TaskItemDto task)
    {
        if (IsBusy || !await CanLeaveAsync()) return false;
        EnterBusy("正在读取任务…");
        try
        {
            var result = await _api.GetAsync(task.Id, _lifetime.Token);
            if (result.IsSuccess)
            {
                Editor.Load(result.Value);
                SetSelectedItem(Items.FirstOrDefault(item => item.Id == result.Value.Id));
                IsEditorOpen = true;
                ClearFeedback();
                return true;
            }
            ShowError(result.Error!);
            await LoadAsync();
            return false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Message = "";
            HasConflict = false;
            Error = $"任务加载失败，请重试。（诊断编号 {Report(ex)}）";
            return false;
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task<bool> SaveAsync()
    {
        if (IsBusy || _disposed) return false;
        if (!Editor.IsDirty) return true;
        ClearFeedback();
        if (!Editor.Validate()) return false;
        EnterBusy("正在保存任务…");
        try
        {
            var result = Editor.Current is { } current
                ? await _api.UpdateAsync(new(current.Id, Editor.Title, Editor.Notes, current.Revision), _lifetime.Token)
                : await _api.CreateAsync(new(Editor.Title, Editor.Notes), _lifetime.Token);
            if (!result.IsSuccess)
            {
                if (!Editor.SetFieldError(result.Error!)) ShowError(result.Error!);
                return false;
            }
            EnsureTaskVisible(result.Value);
            Editor.Load(result.Value);
            ShowMessage("已保存到此电脑");
            await LoadAsync();
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Error = $"保存未完成。请刷新列表确认后再试。（诊断编号 {Report(ex)}）";
            return false;
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task ToggleAsync()
    {
        if (!await CanLeaveAsync() || Editor.Current is not { } current) return;
        EnterBusy("正在更新任务状态…");
        ClearFeedback();
        try
        {
            var result = await _api.SetCompletedAsync(new(current.Id, !current.IsCompleted, current.Revision), _lifetime.Token);
            if (result.IsSuccess)
            {
                EnsureTaskVisible(result.Value);
                Editor.Load(result.Value);
                ShowMessage(result.Value.IsCompleted ? "已标记完成" : "已恢复为未完成");
                await LoadAsync();
            }
            else
            {
                ShowError(result.Error!);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = $"状态修改未完成，请重新加载确认。（诊断编号 {Report(ex)}）";
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task DeleteAsync()
    {
        if (Editor.Current is not { } current || !await _dialogs.ConfirmDeleteAsync(current.Title)) return;
        EnterBusy("正在删除任务…");
        ClearFeedback();
        try
        {
            var result = await _api.DeleteAsync(new(current.Id, current.Revision), _lifetime.Token);
            if (result.IsSuccess)
            {
                Editor.Load(null);
                SetSelectedItem(null);
                IsEditorOpen = false;
                ShowMessage("任务已删除");
                await LoadAsync();
            }
            else
            {
                ShowError(result.Error!);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = $"删除未完成，请刷新列表确认。（诊断编号 {Report(ex)}）";
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task ReloadAsync()
    {
        if (Editor.Current is not { } current) return;
        if (Editor.IsDirty)
        {
            var choice = await _dialogs.AskUnsavedChangesAsync();
            if (choice == UnsavedChoice.Cancel || (choice == UnsavedChoice.Save && !await SaveAsync())) return;
        }
        EnterBusy("正在重新加载任务…");
        ClearFeedback();
        try
        {
            var result = await _api.GetAsync(current.Id, _lifetime.Token);
            if (result.IsSuccess)
            {
                Editor.Load(result.Value);
                ShowMessage("已重新加载");
            }
            else if (result.Error!.Code == ErrorCodes.NotFound)
            {
                Editor.Load(null);
                IsEditorOpen = false;
                ShowError(result.Error);
            }
            else
            {
                ShowError(result.Error!);
            }
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = $"重新加载失败，编辑内容已保留。（诊断编号 {Report(ex)}）";
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task BackToListAsync()
    {
        if (IsBusy) await _idle.Task;
        if (Editor.IsDirty)
        {
            switch (await _dialogs.AskUnsavedChangesAsync())
            {
                case UnsavedChoice.Cancel:
                    return;
                case UnsavedChoice.Save when !await SaveAsync():
                    return;
                case UnsavedChoice.Discard:
                    Editor.Load(Editor.Current);
                    break;
            }
        }
        SetSelectedItem(null);
        IsEditorOpen = false;
    }

    public async Task<bool> CanLeaveAsync()
    {
        if (IsBusy) await _idle.Task;
        if (!Editor.IsDirty) return true;
        return await _dialogs.AskUnsavedChangesAsync() switch
        {
            UnsavedChoice.Discard => true,
            UnsavedChoice.Save => await SaveAsync(),
            _ => false
        };
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        NotifyCommands();
        if (e.PropertyName is nameof(TaskEditorViewModel.Title) or nameof(TaskEditorViewModel.Notes))
            ClearFeedback();
    }

    private void SetFilter(int value, bool load)
    {
        if (value is not (>= 0 and <= 2) || !SetProperty(ref _filter, value, nameof(FilterIndex))) return;
        if (!Editor.IsDirty)
        {
            Editor.Load(null);
            SetSelectedItem(null);
            IsEditorOpen = false;
            ClearFeedback();
        }
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        if (load) LoadCommand.Execute(null);
    }

    private void EnsureTaskVisible(TaskItemDto task)
    {
        var visible = FilterIndex switch
        {
            1 => !task.IsCompleted,
            2 => task.IsCompleted,
            _ => true
        };
        if (!visible) SetFilter(0, load: false);
    }

    private void EnterBusy(string feedback)
    {
        BusyFeedback = feedback;
        IsBusy = true;
    }

    private void ExitBusy()
    {
        IsBusy = false;
        BusyFeedback = "";
    }

    private void ClearFeedback()
    {
        Error = "";
        Message = "";
        HasConflict = false;
    }

    private void ShowMessage(string message)
    {
        Error = "";
        HasConflict = false;
        Message = message;
    }

    private void ApplyListResult(OperationResult<IReadOnlyList<TaskItemDto>> result)
    {
        if (result.IsSuccess)
        {
            var selectedId = Editor.Current?.Id ?? SelectedItem?.Id;
            Items.Clear();
            foreach (var task in result.Value) Items.Add(new TaskListItemViewModel(task));
            SetSelectedItem(selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId));
            ListError = "";
            return;
        }
        if (result.Error!.Code == ErrorCodes.Canceled) return;
        ClearList();
        ListError = result.Error.Message + (result.Error.CorrelationId is null ? "" : $"（诊断编号 {result.Error.CorrelationId}）");
    }

    private void ClearList()
    {
        Items.Clear();
        SetSelectedItem(null);
    }

    private void SetSelectedItem(TaskListItemViewModel? item) =>
        SetProperty(ref _selectedItem, item, nameof(SelectedItem));

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasListError));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CountLabel));
    }

    private void NotifyCommands()
    {
        NewCommand.NotifyCanExecuteChanged();
        SelectCommand.NotifyCanExecuteChanged();
        BackToListCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
    }

    private void ShowError(OperationError error)
    {
        Message = "";
        HasConflict = error.Code == ErrorCodes.Conflict;
        Error = error.Message + (error.CorrelationId is null ? "" : $"（诊断编号 {error.CorrelationId}）");
    }

    private string Report(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try { _diagnostics.Report(correlationId, exception); }
        catch { /* A diagnostics failure must not replace the original UI failure. */ }
        return correlationId;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        _query?.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
