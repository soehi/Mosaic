using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

public sealed record TaskListItemViewModel(
    Guid Id,
    string Title,
    string Notes,
    bool IsCompleted,
    bool IsArchived,
    string StatusText,
    string UpdatedText);

/// <summary>
/// The home screen: cached tasks, filters and sync status. It never writes:
/// phase one is read-only, so every command is a load or a foreground sync.
/// </summary>
public sealed partial class TaskListViewModel : ViewModelBase
{
    private readonly ISyncStore _store;
    private readonly SyncEngine _sync;
    private readonly AuthSessionManager _auth;
    private readonly PortableSettings _settings;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _lastCount;

    public TaskListViewModel(
        ISyncStore store,
        SyncEngine sync,
        AuthSessionManager auth,
        PortableSettings settings,
        LocalizationService localizer)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _sync = sync;
        _auth = auth;
        _settings = settings;

        _auth.StateChanged += OnAuthStateChanged;
        _sync.StatusChanged += OnSyncStatusChanged;
        _sync.Completed += OnSyncCompleted;
        _settings.Changed += OnSettingsChanged;

        Filter = _settings.DefaultFilter switch
        {
            TaskDefaultFilter.Open => MobileTaskFilter.Open,
            TaskDefaultFilter.Completed => MobileTaskFilter.Completed,
            _ => MobileTaskFilter.All,
        };
    }

    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];

    [ObservableProperty]
    private MobileTaskFilter filter;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string lastSyncedText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    public bool IsEmpty => Tasks.Count == 0;

    public bool IsSignedIn => _auth.State == AuthState.SignedIn;

    public bool IsReadOnly => _auth.CacheReadOnly;

    public bool ShowReadOnlyNotice => _auth.CacheReadOnly;

    public string TitleText => T(StringKeys.Tasks);

    public string EmptyText => Filter switch
    {
        MobileTaskFilter.Open => T(StringKeys.NoOpenTasks),
        MobileTaskFilter.Completed => T(StringKeys.NoCompletedTasks),
        MobileTaskFilter.Archived => T(StringKeys.NoArchivedTasks),
        _ => T(StringKeys.NoTasks),
    };

    public string FilterAllText => T(StringKeys.FilterAll);

    public string FilterOpenText => T(StringKeys.FilterOpen);

    public string FilterCompletedText => T(StringKeys.FilterCompleted);

    public string FilterArchivedText => T(StringKeys.FilterArchived);

    public string SyncNowText => T(StringKeys.SyncNow);

    public string ReadOnlyNoticeText => T(StringKeys.ReadOnlyCache);

    public override void Dispose()
    {
        _auth.StateChanged -= OnAuthStateChanged;
        _sync.StatusChanged -= OnSyncStatusChanged;
        _sync.Completed -= OnSyncCompleted;
        _settings.Changed -= OnSettingsChanged;
        _loadGate.Dispose();
        base.Dispose();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            IsBusy = true;
            var items = await _store.ListTasksAsync(Filter).ConfigureAwait(true);

            if (items.Count == 0
                && Filter != MobileTaskFilter.All
                && _settings.AutoSwitchToAllOnHidden
                && _lastCount > 0)
            {
                Filter = MobileTaskFilter.All;
                NoticeText = T(StringKeys.SwitchedToAll);
                items = await _store.ListTasksAsync(Filter).ConfigureAwait(true);
            }
            else
            {
                NoticeText = string.Empty;
            }

            Tasks.Clear();
            foreach (var task in items)
            {
                Tasks.Add(Map(task));
            }

            _lastCount = Tasks.Count;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyText));
        }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_auth.State != AuthState.SignedIn)
        {
            await LoadAsync().ConfigureAwait(true);
            return;
        }

        await _sync.SyncOnceAsync().ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SetFilterAsync(string? filterName)
    {
        if (!Enum.TryParse<MobileTaskFilter>(filterName, ignoreCase: true, out var parsed))
        {
            return;
        }

        Filter = parsed;
        await LoadAsync().ConfigureAwait(true);
    }

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        StatusText = SyncStatusText.Describe(Localizer, _sync.Status);
        LastSyncedText = SyncStatusText.LastSynced(Localizer, _sync.Status.LastSyncedAt);

        // Rebuild rows so their status captions follow the language.
        var snapshot = Tasks.ToList();
        Tasks.Clear();
        foreach (var item in snapshot)
        {
            Tasks.Add(item with
            {
                StatusText = DescribeStatus(item.IsArchived, item.IsCompleted),
                UpdatedText = item.UpdatedText,
            });
        }
    }

    private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(ShowReadOnlyNotice));
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusSnapshot e)
    {
        StatusText = SyncStatusText.Describe(Localizer, e);
        LastSyncedText = SyncStatusText.LastSynced(Localizer, e.LastSyncedAt);
    }

    private void OnSyncCompleted(object? sender, SyncReport e)
    {
        _ = LoadAsync();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(EmptyText));
    }

    private TaskListItemViewModel Map(SyncedTask task) => new(
        task.Id,
        string.IsNullOrWhiteSpace(task.Title) ? "(…)" : task.Title,
        task.Notes,
        task.IsCompleted,
        task.ArchivedAtUtc is not null,
        DescribeStatus(task.ArchivedAtUtc is not null, task.IsCompleted),
        task.UpdatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    private string DescribeStatus(bool archived, bool completed)
    {
        if (archived)
        {
            return T(StringKeys.TaskArchived);
        }

        return completed ? T(StringKeys.TaskCompleted) : T(StringKeys.TaskOpen);
    }
}
