using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

/// <summary>
/// Foreground, read-only incremental sync.
///
/// One run at a time: a pull-to-refresh joins the in-flight run. Pages are
/// applied in a transaction that also stores the cursor. On
/// <c>sync.cursor_invalid</c> the projection is cleared and rebuilt from the
/// latest snapshot; if that also fails the next run simply starts from an empty
/// cursor, and a second invalid cursor in the same run stops instead of looping.
/// </summary>
public sealed class SyncEngine
{
    public const int PageSize = 200;
    public const int MaxPagesPerRun = 20;

    private readonly IMosaicApi _api;
    private readonly ISyncStore _store;
    private readonly BackupRestoreService _backups;
    private readonly PortableSettings _settings;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private TaskCompletionSource<SyncReport>? _running;
    private CancellationTokenSource? _current;
    private SyncStatusSnapshot _status = new(SyncStatusType.Idle, null, 0, null);

    public SyncEngine(
        IMosaicApi api,
        ISyncStore store,
        BackupRestoreService backups,
        PortableSettings settings,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(settings);
        _api = api;
        _store = store;
        _backups = backups;
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<SyncStatusSnapshot>? StatusChanged;

    public event EventHandler<SyncReport>? Completed;

    public SyncStatusSnapshot Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Offline start: apply the cached settings projection and last sync time.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var settings = await _store.ListSettingsAsync(cancellationToken).ConfigureAwait(false);
        _settings.ApplyAll(settings);

        lock (_gate)
        {
            _status = new SyncStatusSnapshot(SyncStatusType.Idle, state.LastSyncedAt, 0, null);
        }

        RaiseStatus();
    }

    /// <summary>Cancel the current run (logout, account switch, background).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    public Task<SyncReport> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<SyncReport> completion;
        lock (_gate)
        {
            if (_running is { } existing)
            {
                return existing.Task;
            }

            completion = new TaskCompletionSource<SyncReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = completion;
        }

        _ = RunTrackedAsync(completion, cancellationToken);
        return completion.Task;
    }

    private async Task RunTrackedAsync(
        TaskCompletionSource<SyncReport> completion,
        CancellationToken cancellationToken)
    {
        var report = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _running = null;
            _current = null;
        }

        completion.TrySetResult(report);
        Completed?.Invoke(this, report);
    }

    private async Task<SyncReport> RunCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _current = linked;
        }

        SetStatus(SyncStatusType.Syncing, 0, null);

        var pages = 0;
        var changes = 0;
        var rebuilt = false;
        var sawCursorInvalid = false;

        try
        {
            var state = await _store.GetStateAsync(linked.Token).ConfigureAwait(false);
            var cursor = state.Cursor;

            while (pages < MaxPagesPerRun)
            {
                linked.Token.ThrowIfCancellationRequested();

                var path =
                    $"v1/sync/changes?cursor={Uri.EscapeDataString(cursor ?? string.Empty)}&limit={PageSize}";
                var result = await _api
                    .GetAsync<SyncChangesResponseDto>(
                        path,
                        authenticated: true,
                        retryTransient: true,
                        linked.Token)
                    .ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    if (result.StatusCode == 401)
                    {
                        SetStatus(SyncStatusType.NeedsSignIn, 0, result.ErrorCode ?? "auth.session_expired");
                        return new SyncReport(false, pages, changes, rebuilt, false, SyncErrorMapper.FromApi(result.StatusCode, result.Error), null);
                    }

                    if (result.StatusCode == 410 || result.ErrorCode == WireErrorCodes.SyncCursorInvalid)
                    {
                        if (sawCursorInvalid)
                        {
                            // Do not loop: report that a manual rebuild is needed.
                            SetStatus(SyncStatusType.RebuildRequired, 0, WireErrorCodes.SyncCursorInvalid);
                            return new SyncReport(false, pages, changes, rebuilt, true, new SyncError(SyncErrorKind.Protocol, WireErrorCodes.SyncCursorInvalid), null);
                        }

                        sawCursorInvalid = true;
                        await _store.ResetProjectionAsync(linked.Token).ConfigureAwait(false);
                        _settings.ResetAll();

                        var restore = await _backups.RestoreLatestAsync(_store, linked.Token).ConfigureAwait(false);
                        if (restore.Restored)
                        {
                            rebuilt = true;
                            cursor = restore.Cursor;
                            changes += restore.TaskCount;
                        }
                        else
                        {
                            // No snapshot: rebuild from an empty cursor next pass.
                            cursor = null;
                        }

                        pages++;
                        continue;
                    }

                    var error = SyncErrorMapper.FromApi(result.StatusCode, result.Error);
                    SetStatus(
                        error.Kind == SyncErrorKind.Offline ? SyncStatusType.Offline : SyncStatusType.Failed,
                        changes,
                        error.Code);
                    return new SyncReport(false, pages, changes, rebuilt, false, error, null);
                }

                var projection = ChangeProjector.Project(result.Value, _time.GetUtcNow());
                linked.Token.ThrowIfCancellationRequested();
                await _store.ApplyPageAsync(projection.Page, linked.Token).ConfigureAwait(false);
                _settings.ApplyAll(projection.Page.Settings);

                changes += projection.ChangeCount;
                cursor = projection.Page.NextCursor ?? cursor;
                pages++;

                if (result.Value?.HasMore != true)
                {
                    break;
                }
            }

            var lastSynced = _time.GetUtcNow();
            SetStatus(SyncStatusType.Succeeded, changes, null);
            lock (_gate)
            {
                _status = _status with { LastSyncedAt = lastSynced };
            }

            RaiseStatus();
            return new SyncReport(true, pages, changes, rebuilt, false, null, lastSynced);
        }
        catch (OperationCanceledException)
        {
            SetStatus(SyncStatusType.Idle, changes, LocalTransportCodes.Cancelled);
            return new SyncReport(false, pages, changes, rebuilt, false, SyncError.Cancelled, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never surface message text: it can contain server prose or paths.
            SetStatus(SyncStatusType.Failed, changes, "sync.local_failure");
            return new SyncReport(
                false,
                pages,
                changes,
                rebuilt,
                false,
                new SyncError(SyncErrorKind.Protocol, "sync.local_failure"),
                null);
        }
    }

    private void SetStatus(SyncStatusType status, int changeCount, string? code)
    {
        lock (_gate)
        {
            _status = new SyncStatusSnapshot(status, _status.LastSyncedAt, changeCount, code);
        }

        RaiseStatus();
    }

    private void RaiseStatus()
    {
        StatusChanged?.Invoke(this, Status);
    }
}
