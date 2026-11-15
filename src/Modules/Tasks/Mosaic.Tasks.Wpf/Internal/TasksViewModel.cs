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
    private readonly Mosaic.Settings.Contracts.ISettingsCore? _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _query;
    private long _queryVersion;
    private bool _busy;
    private string _busyFeedback = "";
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _settingsRegistration;
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

    internal TasksViewModel(
        ITasksApi api,
        IUiDispatcher dispatcher,
        IUserDialogService dialogs,
        IDiagnosticSink diagnostics,
        Mosaic.Settings.Contracts.ISettingsCore? settings = null)
    {
        _api = api;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        _settings = settings;
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
        if (settings is not null)
        {
            _settingsRegistration = new CompositeRegistration(
                settings.Register(new TaskSettingSource("tasks-ui", dispatcher, ApplySettings)),
                settings.Register(new TaskSettingSource("tasks-core", dispatcher, ApplySettings)));
        }
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
    public double CompactActionsWidth => _layout.CompactActionsWidth;
    public double CompactActionsHeight => _layout.CompactActionsHeight;
    public double ListMinColumnWidth => _layout.ListMinColumnWidth;
    public double NotesMinHeight => _layout.NotesMinHeight;
    public double TaskButtonMinWidth => _layout.TaskButtonMinWidth;
    public double TaskButtonMinHeight => _layout.TaskButtonMinHeight;
    public bool ShowListMessage => HasMessage && !IsEditorOpen;
    public string CountLabel => FilterIndex switch
    {
        1 => $"{Items.Count} 项未完成",
        2 => $"{Items.Count} 项已完成",
        _ => $"{Items.Count} 项任务"
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
        if (_settings is not null)
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
            Error = $"状态修改未完成，请重新加载确认。（诊断编号 {Report(ex)}）";
        }
        finally
        {
            ExitBusy();
        }
    }

    private async Task DeleteAsync()
    {
        if (Editor.Current is not { } current || (_confirmDelete && !await _dialogs.ConfirmDeleteAsync(current.Title))) return;
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
            foreach (var task in result.Value) Items.Add(new TaskListItemViewModel(task, _text.UntitledTask));
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

    private sealed class TaskSettingSource(
        string moduleId,
        IUiDispatcher dispatcher,
        Action<IReadOnlyDictionary<string, object?>> apply) : Mosaic.Settings.Contracts.ISettingSource
    {
        public string ModuleId => moduleId;
        public IReadOnlyList<Mosaic.Settings.Contracts.SettingDefinition> Definitions { get; } =
            moduleId == "tasks-ui"
                ? [
                    new("tasks.confirmDelete", "tasks-ui", "删除前确认", "删除任务前显示确认框", Mosaic.Settings.Contracts.SettingValueKind.Boolean, true, null, null, null, Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.confirmUnsaved", "tasks-ui", "未保存离开确认", "离开编辑器前确认未保存内容", Mosaic.Settings.Contracts.SettingValueKind.Boolean, true, null, null, null, Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.defaultFilter", "tasks-ui", "默认筛选", "打开任务页时使用的筛选", Mosaic.Settings.Contracts.SettingValueKind.Enum, "All", null, null, ["All", "Open", "Completed"], Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.showCharacterCount", "tasks-ui", "显示字符计数", "在任务编辑器中显示字符数", Mosaic.Settings.Contracts.SettingValueKind.Boolean, true, null, null, null, Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.autoSwitchToAllOnHidden", "tasks-ui", "隐藏任务自动切回全部", "当前筛选下任务不可见时自动切回全部", Mosaic.Settings.Contracts.SettingValueKind.Boolean, true, null, null, null, Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.layout.compactBreakpoint", "tasks-ui", "任务窄屏断点", "低于该宽度时列表与详情单页切换", Mosaic.Settings.Contracts.SettingValueKind.Integer, 920, 600, 1400, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.editor.compactActionsWidth", "tasks-ui", "操作栏窄宽断点", "低于该宽度时操作栏换行", Mosaic.Settings.Contracts.SettingValueKind.Integer, 480, 360, 900, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.editor.compactActionsHeight", "tasks-ui", "操作栏矮窗断点", "低于该高度时操作栏换行", Mosaic.Settings.Contracts.SettingValueKind.Integer, 680, 480, 1000, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.list.minColumnWidth", "tasks-ui", "列表最小列宽", "并排布局中任务列表最小宽度", Mosaic.Settings.Contracts.SettingValueKind.Integer, 300, 220, 500, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.editor.notesMinHeight", "tasks-ui", "备注最小高度", "任务备注输入框最小高度", Mosaic.Settings.Contracts.SettingValueKind.Integer, 128, 96, 360, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.button.minWidth", "tasks-ui", "按钮最小宽度", "任务操作按钮最小宽度", Mosaic.Settings.Contracts.SettingValueKind.Integer, 96, 72, 160, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.button.minHeight", "tasks-ui", "按钮最小高度", "任务操作按钮最小高度", Mosaic.Settings.Contracts.SettingValueKind.Integer, 40, 32, 56, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings)
                  ]
                : [
                    new("tasks.sortOrder", "tasks-core", "任务排序", "任务列表默认排序方式", Mosaic.Settings.Contracts.SettingValueKind.Enum, "CreatedDesc", null, null, ["CreatedDesc", "CreatedAsc", "TitleAsc", "TitleDesc"], Mosaic.Settings.Contracts.SettingCategory.Normal, Mosaic.Settings.Contracts.SettingApplyMode.Immediate, Mosaic.Settings.Contracts.SettingPersistence.UserSettings),
                    new("tasks.title.maxCharacters", "tasks-core", "标题上限", "任务标题允许的最大字符数", Mosaic.Settings.Contracts.SettingValueKind.Integer, 200, 1, 1000, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Restart, Mosaic.Settings.Contracts.SettingPersistence.UserSettings, "收紧后旧任务仍可读，编辑超限需截断"),
                    new("tasks.notes.maxCharacters", "tasks-core", "备注上限", "任务备注允许的最大字符数", Mosaic.Settings.Contracts.SettingValueKind.Integer, 20000, 1000, 200000, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Restart, Mosaic.Settings.Contracts.SettingPersistence.UserSettings, "过大会增加编辑与保存负担"),
                    new("tasks.title.required", "tasks-core", "标题必填", "新建任务是否必须填写标题", Mosaic.Settings.Contracts.SettingValueKind.Boolean, true, null, null, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Restart, Mosaic.Settings.Contracts.SettingPersistence.UserSettings, "关闭后允许空标题"),
                    new("tasks.defaultCompleted", "tasks-core", "新任务默认完成", "新建任务是否直接标记完成", Mosaic.Settings.Contracts.SettingValueKind.Boolean, false, null, null, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Restart, Mosaic.Settings.Contracts.SettingPersistence.UserSettings, "新任务可能不在“未完成”筛选出现"),
                    new("tasks.revisionStart", "tasks-core", "版本起点", "新建任务的初始版本号", Mosaic.Settings.Contracts.SettingValueKind.Integer, 1, 1, 1000000, null, Mosaic.Settings.Contracts.SettingCategory.Advanced, Mosaic.Settings.Contracts.SettingApplyMode.Restart, Mosaic.Settings.Contracts.SettingPersistence.UserSettings, "仅影响新任务初始版本")
                  ];
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
            dispatcher.CheckAccess() ? ApplyCore(values) : dispatcher.InvokeAsync(() => ApplyCore(values));

        private Task ApplyCore(IReadOnlyDictionary<string, object?> values) { apply(values); return Task.CompletedTask; }
    }

    private sealed class CompositeRegistration(params IDisposable[] registrations) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var registration in registrations) registration.Dispose();
        }
    }

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
        _settingsRegistration?.Dispose();
        _lifetime.Cancel();
        _query?.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
