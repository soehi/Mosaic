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
    private readonly ProjectionSession _session;
    private readonly AuthSessionManager _auth;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public TaskListViewModel(
        ProjectionSession session,
        AuthSessionManager auth,
        LocalizationService localizer)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(auth);
        _session = session;
        _auth = auth;

        _auth.StateChanged += OnAuthStateChanged;
        _session.StatusChanged += OnSyncStatusChanged;
        _session.Completed += OnSyncCompleted;
        _session.Changed += OnProjectionChanged;

        // Filters are per view state in v2: PortableSettings was removed with
        // the write path, so the home screen always starts from "all".
        Filter = MobileTaskFilter.All;
        StatusText = SyncStatusText.Describe(Localizer, _session.Status);
        LastSyncedText = SyncStatusText.LastSynced(Localizer, _session.Status.LastSyncedAt);
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
        _session.StatusChanged -= OnSyncStatusChanged;
        _session.Completed -= OnSyncCompleted;
        _session.Changed -= OnProjectionChanged;
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

            // Snapshot the store once: an account switch can replace it while the
            // query is in flight, and rows from the previous projection must never
            // be shown for the new account.
            var store = _session.Store;
            var items = store is null
                ? Array.Empty<SyncedTask>()
                : (await store.ListTasksAsync(Filter).ConfigureAwait(true)).ToArray();

            if (!ReferenceEquals(store, _session.Store))
            {
                return;
            }

            Tasks.Clear();
            foreach (var task in items)
            {
                Tasks.Add(Map(task));
            }

            NoticeText = ProjectionStatusText.Describe(Localizer, _session.Failure);
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
        var engine = _session.Engine;
        if (_auth.State != AuthState.SignedIn || engine is null)
        {
            await LoadAsync().ConfigureAwait(true);
            return;
        }

        await engine.SyncOnceAsync().ConfigureAwait(true);
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
        NoticeText = ProjectionStatusText.Describe(Localizer, _session.Failure);
        StatusText = SyncStatusText.Describe(Localizer, _session.Status);
        LastSyncedText = SyncStatusText.LastSynced(Localizer, _session.Status.LastSyncedAt);

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

    private void OnProjectionChanged(object? sender, EventArgs e)
    {
        // Opening, switching or closing the projection all invalidate the rows
        // currently on screen, so reload instead of keeping stale tasks.
        _ = LoadAsync();
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
