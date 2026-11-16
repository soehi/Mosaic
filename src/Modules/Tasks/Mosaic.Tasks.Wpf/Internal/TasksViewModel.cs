using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TasksViewModel : ObservableObject, IPageController
{
    private readonly ITasksApi _api;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserDialogService _dialogs;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Mosaic.Settings.Contracts.ISettingsApi? _settings;
    private readonly TaskUiSettingsTarget? _settingsTarget;
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
    private bool _showCharacterCount = true;
    private bool _confirmDelete = true;
    private bool _confirmUnsaved = true;
    private bool _autoSwitchToAllOnHidden = true;
    private bool _defaultFilterApplied;
    private TaskSortOrder _sortOrder = TaskSortOrder.CreatedDesc;
    private TaskTextSettings _text = TaskTextSettings.Defaults;
    private TasksLayoutSettings _layout = TasksLayoutSettings.Defaults;
    private TasksFeedbackKind _busyKind = TasksFeedbackKind.None;
    private TasksFeedbackKind _listErrorKind = TasksFeedbackKind.None;
    private string? _listErrorApiMessage;
    private string? _listErrorCorrelationId;
    private TasksFeedbackKind _errorKind = TasksFeedbackKind.None;
    private string? _errorApiMessage;
    private string? _errorCorrelationId;
    private TasksFeedbackKind _messageKind = TasksFeedbackKind.None;
    private string? _messageSettingsText;

    internal TasksViewModel(
        ITasksApi api,
        IUiDispatcher dispatcher,
        IUserDialogService dialogs,
        IDiagnosticSink diagnostics,
        Mosaic.Settings.Contracts.ISettingsApi? settings = null,
        TaskUiSettingsTarget? settingsTarget = null)
    {
        _api = api;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        _settings = settings;
        _settingsTarget = settingsTarget;
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
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
        settingsTarget?.Attach(ApplySettings);
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
    public bool ShowCharacterCount => _showCharacterCount;
    public double CompactBreakpoint => _layout.CompactBreakpoint;
    public double EditorPaneWidth { get; private set; } = 480;
    public double PaneGap { get; private set; } = 24;
    public double CompactActionsWidth => _layout.CompactActionsWidth;
    public double CompactActionsHeight => _layout.CompactActionsHeight;
    public double ListMinColumnWidth => _layout.ListMinColumnWidth;
    public double NotesMinHeight => _layout.NotesMinHeight;
    public double TaskButtonMinWidth => _layout.TaskButtonMinWidth;
    public double TaskButtonMinHeight => _layout.TaskButtonMinHeight;
    public bool ShowListMessage => HasMessage && !IsEditorOpen;
    public string CountLabel => FilterIndex switch
    {
        1 => TaskLocalization.Format("Tasks.Count.Open", "{0} 项未完成", Items.Count),
        2 => TaskLocalization.Format("Tasks.Count.Completed", "{0} 项已完成", Items.Count),
        _ => Items.Count == 1
            ? TaskLocalization.Localize("Tasks.Count.All.One", "1 项任务")
            : TaskLocalization.Format("Tasks.Count.All", "{0} 项任务", Items.Count)
    };
    public string EmptyTitle => FilterIndex switch { 1 => _text.EmptyOpenTitle, 2 => _text.EmptyCompletedTitle, _ => _text.EmptyAllTitle };
    public string EmptyDescription => FilterIndex switch { 1 => _text.EmptyOpenDescription, 2 => _text.EmptyCompletedDescription, _ => _text.EmptyAllDescription };
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

    public async Task InitializeAsync()
    {
        if (_settings is not null && _settingsTarget?.HasValues != true)
        {
            var values = await _settings.GetValuesAsync(_lifetime.Token);
            if (values.IsSuccess) await _dispatcher.InvokeAsync(() => ApplySettings(values.Value));
        }
        var constraints = await _api.GetConstraintsAsync(_lifetime.Token);
        if (constraints.IsSuccess) await _dispatcher.InvokeAsync(() => Editor.ApplyConstraints(constraints.Value));
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_disposed) return;
        var version = ++_queryVersion;
        _query?.Cancel();
        var query = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _query = query;
        ListError = "";
        _listErrorKind = TasksFeedbackKind.None;
        IsLoading = true;
        try
        {
            var result = await _api.ListAsync(new((TaskFilter)FilterIndex, _sortOrder), query.Token);
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
                SetListError(TasksFeedbackKind.LoadFailed, null, correlationId);
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
        EnterBusy(TasksFeedbackKind.Loading);
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
            _messageKind = TasksFeedbackKind.None;
            HasConflict = false;
            SetError(TasksFeedbackKind.OpenFailed, null, Report(ex));
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
        EnterBusy(TasksFeedbackKind.Saving);
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
            ShowMessage(_text.SaveSuccess);
            await LoadAsync();
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetError(TasksFeedbackKind.SaveFailed, null, Report(ex));
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
        EnterBusy(TasksFeedbackKind.Updating);
        ClearFeedback();
        try
        {
            var result = await _api.SetCompletedAsync(new(current.Id, !current.IsCompleted, current.Revision), _lifetime.Token);
            if (result.IsSuccess)
            {
                EnsureTaskVisible(result.Value);
                Editor.Load(result.Value);
                ShowMessage(result.Value.IsCompleted ? _text.CompletedSuccess : _text.RestoredSuccess);
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
            SetError(TasksFeedbackKind.ToggleFailed, null, Report(ex));
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task DeleteAsync()
    {
        if (Editor.Current is not { } current || (_confirmDelete && !await _dialogs.ConfirmDeleteAsync(current.Title))) return;
        EnterBusy(TasksFeedbackKind.Deleting);
        ClearFeedback();
        try
        {
            var result = await _api.DeleteAsync(new(current.Id, current.Revision), _lifetime.Token);
            if (result.IsSuccess)
            {
                Editor.Load(null);
                SetSelectedItem(null);
                IsEditorOpen = false;
                ShowMessage(_text.DeleteSuccess);
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
            SetError(TasksFeedbackKind.DeleteFailed, null, Report(ex));
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
            if (!_confirmUnsaved)
            {
                Editor.Load(Editor.Current);
            }
            else
            {
            var choice = await _dialogs.AskUnsavedChangesAsync();
            if (choice == UnsavedChoice.Cancel || (choice == UnsavedChoice.Save && !await SaveAsync())) return;
            }
        }
        EnterBusy(TasksFeedbackKind.Reloading);
        ClearFeedback();
        try
        {
            var result = await _api.GetAsync(current.Id, _lifetime.Token);
            if (result.IsSuccess)
            {
                Editor.Load(result.Value);
                ShowReloadedMessage();
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
            SetError(TasksFeedbackKind.ReloadFailed, null, Report(ex));
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
            if (!_confirmUnsaved)
            {
                Editor.Load(Editor.Current);
                SetSelectedItem(null);
                IsEditorOpen = false;
                return;
            }
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
        if (!Editor.IsDirty || !_confirmUnsaved) return true;
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
        if (!visible && _autoSwitchToAllOnHidden) SetFilter(0, load: false);
    }

    private void EnterBusy(TasksFeedbackKind kind)
    {
        _busyKind = kind;
        BusyFeedback = RenderBusy(kind);
        IsBusy = true;
    }

    private void ExitBusy()
    {
        IsBusy = false;
        _busyKind = TasksFeedbackKind.None;
        BusyFeedback = "";
    }

    private void ClearFeedback()
    {
        Error = "";
        Message = "";
        _errorKind = TasksFeedbackKind.None;
        _messageKind = TasksFeedbackKind.None;
        HasConflict = false;
    }

    private void ShowMessage(string message)
    {
        Error = "";
        _errorKind = TasksFeedbackKind.None;
        HasConflict = false;
        _messageKind = TasksFeedbackKind.SettingsText;
        _messageSettingsText = message;
        Message = message;
    }

    private void ShowReloadedMessage()
    {
        Error = "";
        _errorKind = TasksFeedbackKind.None;
        HasConflict = false;
        _messageKind = TasksFeedbackKind.Reloaded;
        _messageSettingsText = null;
        Message = TaskLocalization.Localize("Tasks.Message.Reloaded", "已重新加载");
    }

    private void SetListError(TasksFeedbackKind kind, string? apiMessage, string? correlationId)
    {
        _listErrorKind = kind;
        _listErrorApiMessage = apiMessage;
        _listErrorCorrelationId = correlationId;
        ListError = RenderError(kind, apiMessage, correlationId);
    }

    private void SetError(TasksFeedbackKind kind, string? apiMessage, string? correlationId)
    {
        _errorKind = kind;
        _errorApiMessage = apiMessage;
        _errorCorrelationId = correlationId;
        Error = RenderError(kind, apiMessage, correlationId);
    }

    private void ApplyListResult(OperationResult<IReadOnlyList<TaskItemDto>> result)
    {
        if (result.IsSuccess)
        {
            var selectedId = Editor.Current?.Id ?? SelectedItem?.Id;
            Items.Clear();
            foreach (var task in result.Value) Items.Add(new TaskListItemViewModel(task, _text.UntitledTask));
            SetSelectedItem(selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId));
            ListError = "";
            _listErrorKind = TasksFeedbackKind.None;
            return;
        }
        if (result.Error!.Code == ErrorCodes.Canceled) return;
        ClearList();
        SetListError(TasksFeedbackKind.Api, TaskErrorLocalizer.Localize(result.Error), result.Error.CorrelationId);
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
        _messageKind = TasksFeedbackKind.None;
        HasConflict = error.Code == ErrorCodes.Conflict;
        SetError(TasksFeedbackKind.Api, TaskErrorLocalizer.Localize(error, Editor.Current is null ? null : Editor.Constraints), error.CorrelationId);
    }

    private string Report(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try { _diagnostics.Report(correlationId, exception); }
        catch { /* A diagnostics failure must not replace the original UI failure. */ }
        return correlationId;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(RefreshLocalizedText);
            return;
        }
        RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        if (IsBusy && _busyKind != TasksFeedbackKind.None)
            BusyFeedback = RenderBusy(_busyKind);
        if (_listErrorKind != TasksFeedbackKind.None)
            ListError = RenderError(_listErrorKind, _listErrorApiMessage, _listErrorCorrelationId);
        if (_errorKind != TasksFeedbackKind.None)
            Error = RenderError(_errorKind, _errorApiMessage, _errorCorrelationId);
        if (_messageKind != TasksFeedbackKind.None)
            Message = RenderMessage();
        Editor.RefreshLocalizedText();
        RebuildListTitles();
        NotifyListState();
    }

    private string RenderBusy(TasksFeedbackKind kind) => kind switch
    {
        TasksFeedbackKind.Loading => TaskLocalization.Localize("Tasks.Loading", "正在读取任务…"),
        TasksFeedbackKind.Saving => TaskLocalization.Localize("Tasks.Busy.Saving", "正在保存任务…"),
        TasksFeedbackKind.Updating => TaskLocalization.Localize("Tasks.Busy.Updating", "正在更新任务状态…"),
        TasksFeedbackKind.Deleting => TaskLocalization.Localize("Tasks.Busy.Deleting", "正在删除任务…"),
        TasksFeedbackKind.Reloading => TaskLocalization.Localize("Tasks.Busy.Reloading", "正在重新加载任务…"),
        _ => string.Empty
    };

    private string RenderError(TasksFeedbackKind kind, string? apiMessage, string? correlationId)
    {
        var message = kind switch
        {
            TasksFeedbackKind.Api => apiMessage ?? string.Empty,
            TasksFeedbackKind.LoadFailed => TaskLocalization.Localize("Tasks.Error.LoadFailed", "无法加载任务，请刷新后重试。"),
            TasksFeedbackKind.OpenFailed => TaskLocalization.Localize("Tasks.Error.OpenFailed", "任务加载失败，请重试。"),
            TasksFeedbackKind.SaveFailed => TaskLocalization.Localize("Tasks.Error.SaveFailed", "保存未完成。请刷新列表确认后再试。"),
            TasksFeedbackKind.ToggleFailed => TaskLocalization.Localize("Tasks.Error.ToggleFailed", "状态修改未完成，请重新加载确认。"),
            TasksFeedbackKind.DeleteFailed => TaskLocalization.Localize("Tasks.Error.DeleteFailed", "删除未完成，请刷新列表确认。"),
            TasksFeedbackKind.ReloadFailed => TaskLocalization.Localize("Tasks.Error.ReloadFailed", "重新加载失败，编辑内容已保留。"),
            _ => string.Empty
        };
        return string.IsNullOrEmpty(correlationId)
            ? message
            : message + TaskLocalization.Format("Tasks.Error.Diagnostic", "（诊断编号 {0}）", correlationId);
    }

    private string RenderMessage() => _messageKind switch
    {
        TasksFeedbackKind.Reloaded => TaskLocalization.Localize("Tasks.Message.Reloaded", "已重新加载"),
        TasksFeedbackKind.SettingsText => _messageSettingsText ?? string.Empty,
        _ => string.Empty
    };

    private enum TasksFeedbackKind
    {
        None,
        Loading,
        Saving,
        Updating,
        Deleting,
        Reloading,
        Api,
        LoadFailed,
        OpenFailed,
        SaveFailed,
        ToggleFailed,
        DeleteFailed,
        ReloadFailed,
        Reloaded,
        SettingsText
    }

    private void ApplySettings(IReadOnlyDictionary<string, object?> values)
    {
        if (_disposed) return;
        _confirmDelete = ReadBoolean(values, "tasks.confirmDelete", _confirmDelete);
        _confirmUnsaved = ReadBoolean(values, "tasks.confirmUnsaved", _confirmUnsaved);
        _showCharacterCount = ReadBoolean(values, "tasks.showCharacterCount", _showCharacterCount);
        _autoSwitchToAllOnHidden = ReadBoolean(values, "tasks.autoSwitchToAllOnHidden", _autoSwitchToAllOnHidden);

        if (!_defaultFilterApplied)
        {
            var filter = ReadString(values, "tasks.defaultFilter", "All") switch { "Open" => 1, "Completed" => 2, _ => 0 };
            if (_filter != filter)
            {
                _filter = filter;
                OnPropertyChanged(nameof(FilterIndex));
            }
            _defaultFilterApplied = true;
        }

        var sortOrder = ReadString(values, "tasks.sortOrder", _sortOrder.ToString()) switch
        {
            "CreatedAsc" => TaskSortOrder.CreatedAsc,
            "TitleAsc" => TaskSortOrder.TitleAsc,
            "TitleDesc" => TaskSortOrder.TitleDesc,
            _ => TaskSortOrder.CreatedDesc
        };
        var sortChanged = _sortOrder != sortOrder;
        _sortOrder = sortOrder;

        var previousUntitled = _text.UntitledTask;
        _text = new(
            ReadString(values, "ui.text.emptyAllTitle", TaskTextSettings.Defaults.EmptyAllTitle),
            ReadString(values, "ui.text.emptyAllDescription", TaskTextSettings.Defaults.EmptyAllDescription),
            ReadString(values, "ui.text.emptyOpenTitle", TaskTextSettings.Defaults.EmptyOpenTitle),
            ReadString(values, "ui.text.emptyOpenDescription", TaskTextSettings.Defaults.EmptyOpenDescription),
            ReadString(values, "ui.text.emptyCompletedTitle", TaskTextSettings.Defaults.EmptyCompletedTitle),
            ReadString(values, "ui.text.emptyCompletedDescription", TaskTextSettings.Defaults.EmptyCompletedDescription),
            ReadString(values, "ui.text.untitledTask", TaskTextSettings.Defaults.UntitledTask),
            ReadString(values, "ui.text.saveSuccess", TaskTextSettings.Defaults.SaveSuccess),
            ReadString(values, "ui.text.deleteSuccess", TaskTextSettings.Defaults.DeleteSuccess),
            ReadString(values, "ui.text.completedSuccess", TaskTextSettings.Defaults.CompletedSuccess),
            ReadString(values, "ui.text.restoredSuccess", TaskTextSettings.Defaults.RestoredSuccess));
        EditorPaneWidth = ReadDouble(values, "tasks.layout.editorPaneWidth", 480);
        PaneGap = ReadDouble(values, "tasks.layout.paneGap", 24);
        OnPropertyChanged(nameof(EditorPaneWidth));
        OnPropertyChanged(nameof(PaneGap));
        _layout = new(
            ReadDouble(values, "tasks.layout.compactBreakpoint", TasksLayoutSettings.Defaults.CompactBreakpoint),
            ReadDouble(values, "tasks.editor.compactActionsWidth", TasksLayoutSettings.Defaults.CompactActionsWidth),
            ReadDouble(values, "tasks.editor.compactActionsHeight", TasksLayoutSettings.Defaults.CompactActionsHeight),
            ReadDouble(values, "tasks.list.minColumnWidth", TasksLayoutSettings.Defaults.ListMinColumnWidth),
            ReadDouble(values, "tasks.editor.notesMinHeight", TasksLayoutSettings.Defaults.NotesMinHeight),
            ReadDouble(values, "tasks.button.minWidth", TasksLayoutSettings.Defaults.TaskButtonMinWidth),
            ReadDouble(values, "tasks.button.minHeight", TasksLayoutSettings.Defaults.TaskButtonMinHeight));

        foreach (var name in new[]
        {
            nameof(ShowCharacterCount),
            nameof(CompactBreakpoint),
            nameof(CompactActionsWidth),
            nameof(CompactActionsHeight),
            nameof(ListMinColumnWidth),
            nameof(NotesMinHeight),
            nameof(TaskButtonMinWidth),
            nameof(TaskButtonMinHeight),
            nameof(EmptyTitle),
            nameof(EmptyDescription)
        })
            OnPropertyChanged(name);

        if (previousUntitled != _text.UntitledTask) RebuildListTitles();
        if (sortChanged && _defaultFilterApplied) LoadCommand.Execute(null);
    }

    private void RebuildListTitles()
    {
        var selectedId = SelectedItem?.Id;
        var tasks = Items.Select(item => item.Item).ToArray();
        Items.Clear();
        foreach (var task in tasks) Items.Add(new TaskListItemViewModel(task, _text.UntitledTask));
        SetSelectedItem(selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId));
    }

    private static bool ReadBoolean(IReadOnlyDictionary<string, object?> values, string id, bool fallback) =>
        values.TryGetValue(id, out var value) && value is bool boolean ? boolean : fallback;

    private static double ReadDouble(IReadOnlyDictionary<string, object?> values, string id, double fallback) =>
        values.TryGetValue(id, out var value) && value is not null
            ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;

    private static string ReadString(IReadOnlyDictionary<string, object?> values, string id, string fallback) =>
        values.TryGetValue(id, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text : fallback;

    private sealed record TaskTextSettings(
        string EmptyAllTitle,
        string EmptyAllDescription,
        string EmptyOpenTitle,
        string EmptyOpenDescription,
        string EmptyCompletedTitle,
        string EmptyCompletedDescription,
        string UntitledTask,
        string SaveSuccess,
        string DeleteSuccess,
        string CompletedSuccess,
        string RestoredSuccess)
    {
        internal static TaskTextSettings Defaults { get; } = new(
            "这里还没有任务",
            "新建一项任务，给今天一个开始。",
            "没有未完成任务",
            "新建任务或恢复一项任务后，会显示在这里。",
            "还没有已完成的任务",
            "完成一项任务后，会显示在这里。",
            "未命名任务",
            "已保存到此电脑",
            "任务已删除",
            "已标记完成",
            "已恢复为未完成");
    }

    private sealed record TasksLayoutSettings(
        double CompactBreakpoint,
        double CompactActionsWidth,
        double CompactActionsHeight,
        double ListMinColumnWidth,
        double NotesMinHeight,
        double TaskButtonMinWidth,
        double TaskButtonMinHeight)
    {
        internal static TasksLayoutSettings Defaults { get; } = new(920, 480, 680, 300, 128, 96, 40);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
        _settingsTarget?.Detach();
        _lifetime.Cancel();
        _query?.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
