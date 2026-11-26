using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync;

/// <summary>
/// Foreground, read-only incremental sync over the per-account projection.
///
/// One run at a time: a pull-to-refresh joins the in-flight run. A run with no
/// cursor bootstraps from the latest verified snapshot first (or starts from the
/// server's beginning when no snapshot exists), then pulls pages until
/// <c>hasMore=false</c> or the page budget is spent. Each page is committed in
/// one transaction together with its cursor, so the stored cursor can never run
/// ahead of the stored rows.
/// </summary>
public sealed class SyncEngine
{
    public const int PageSize = 200;
    public const int MaxPagesPerRun = 20;

    private readonly IMosaicApi _api;
    private readonly ISyncStore _store;
    private readonly SnapshotBootstrapService _bootstrap;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private TaskCompletionSource<SyncReport>? _running;
    private CancellationTokenSource? _current;
    private SyncStatusSnapshot _status = new(SyncStatusType.Idle, null, 0, null);

    public SyncEngine(
        IMosaicApi api,
        ISyncStore store,
        SnapshotBootstrapService bootstrap,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bootstrap);
        _api = api;
        _store = store;
        _bootstrap = bootstrap;
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

    /// <summary>
    /// Offline start: reflect the persisted projection freshness and the last
    /// completed run without touching the network.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.GetStateAsync(cancellationToken).ConfigureAwait(false);

        var status = state.Freshness switch
        {
            ProjectionFreshness.NeedsRestore => SyncStatusType.NeedsRestore,
            ProjectionFreshness.SnapshotTooLarge => SyncStatusType.SnapshotTooLarge,
            _ => SyncStatusType.Idle,
        };

        lock (_gate)
        {
            _status = new SyncStatusSnapshot(status, state.LastCompleteSyncAt, 0, null);
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
        SyncReport report;
        try
        {
            report = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // RunCoreAsync already maps ordinary failures; this is the last
            // guard so a single run can never leave the single-flight latch on.
            SetStatus(SyncStatusType.Failed, 0, "sync.local_failure");
            report = new SyncReport(
                false,
                0,
                0,
                false,
                false,
                new SyncError(SyncErrorKind.Protocol, "sync.local_failure"),
                null);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _current = null;
            }
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

        var token = linked.Token;
        var pages = 0;
        var changes = 0;
        var bootstrapped = false;
        var incomplete = false;
        var startedWithoutCursor = false;

        try
        {
            var state = await _store.GetStateAsync(token).ConfigureAwait(false);
            var cursor = state.Cursor;
            startedWithoutCursor = cursor is null;

            if (cursor is null)
            {
                SetStatus(SyncStatusType.Bootstrapping, changes, null);

                var (bootstrap, restored) = await RunBootstrapAsync(token).ConfigureAwait(false);
                if (bootstrap.Outcome == SnapshotBootstrapOutcome.Restored)
                {
                    bootstrapped = true;
                    changes += bootstrap.TaskCount;
                    cursor = restored.Cursor;
                }
                else if (bootstrap.Outcome != SnapshotBootstrapOutcome.NoSnapshot)
                {
                    return await FailBootstrapAsync(bootstrap, pages, changes, token).ConfigureAwait(false);
                }
            }

            if (!bootstrapped)
            {
                SetStatus(SyncStatusType.Syncing, changes, null);
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // A first-ever run without a snapshot must not send an empty
                // `cursor=` parameter: the server treats that as a malformed
                // cursor, not as "start from the beginning".
                var path = cursor is null
                    ? $"v1/sync/changes?limit={PageSize}"
                    : $"v1/sync/changes?cursor={Uri.EscapeDataString(cursor)}&limit={PageSize}";

                var result = await _api
                    .GetAsync<SyncChangesResponseDto>(
                        path,
                        authenticated: true,
                        retryTransient: true,
                        token)
                    .ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    if (result.StatusCode == 401)
                    {
                        SetStatus(SyncStatusType.NeedsSignIn, changes, result.ErrorCode);
                        return new SyncReport(
                            false,
                            pages,
                            changes,
                            bootstrapped,
                            false,
                            new SyncError(
                                SyncErrorKind.SessionExpired,
                                result.ErrorCode ?? WireErrorCodes.AuthSessionExpired),
                            null);
                    }

                    if (cursor is not null && IsCursorInvalid(result))
                    {
                        // Rebuild at most once per run. A second rejected cursor
                        // means the snapshot itself is unusable; stop instead of
                        // looping.
                        if (bootstrapped)
                        {
                            return await FailRestoreAsync(pages, changes, bootstrapped, token)
                                .ConfigureAwait(false);
                        }

                        SetStatus(SyncStatusType.Bootstrapping, changes, null);

                        var (bootstrap, rebuilt) = await RunBootstrapAsync(token).ConfigureAwait(false);
                        if (bootstrap.Outcome == SnapshotBootstrapOutcome.Restored)
                        {
                            bootstrapped = true;
                            changes += bootstrap.TaskCount;
                            cursor = rebuilt.Cursor;
                            continue;
                        }

                        if (bootstrap.Outcome == SnapshotBootstrapOutcome.NoSnapshot)
                        {
                            // The local cursor is unknown to the server and no
                            // snapshot exists to replace it: never fall back to
                            // an unverified empty cursor.
                            return await FailRestoreAsync(pages, changes, bootstrapped, token)
                                .ConfigureAwait(false);
                        }

                        return await FailBootstrapAsync(bootstrap, pages, changes, token)
                            .ConfigureAwait(false);
                    }

                    var error = SyncErrorMapper.FromApi(result.StatusCode, result.Error);
                    SetStatus(
                        error.Kind == SyncErrorKind.Offline ? SyncStatusType.Offline : SyncStatusType.Failed,
                        changes,
                        error.Code);
                    return new SyncReport(false, pages, changes, bootstrapped, false, error, null);
                }

                var projection = ChangeProjector.Project(result.Value, _time.GetUtcNow(), cursor);
                token.ThrowIfCancellationRequested();
                await _store.ApplyPageAsync(projection.Page, token).ConfigureAwait(false);

                pages++;
                changes += projection.ChangeCount;
                cursor = projection.Page.NextCursor;

                if (!projection.Page.HasMore)
                {
                    break;
                }

                if (pages >= MaxPagesPerRun)
                {
                    incomplete = true;
                    break;
                }
            }

            if (incomplete)
            {
                // The cursor advanced but the projection has not caught up. The
                // freshness stays "syncing" and the run is not marked complete.
                SetStatus(SyncStatusType.Incomplete, changes, null);
                return new SyncReport(true, pages, changes, bootstrapped, true, null, null);
            }

            var completedAt = _time.GetUtcNow();
            await _store.CompleteSyncAsync(completedAt, token).ConfigureAwait(false);

            var finalStatus = startedWithoutCursor && changes == 0 && !bootstrapped
                ? SyncStatusType.Empty
                : SyncStatusType.Ready;
            SetStatus(finalStatus, changes, null, completedAt);

            return new SyncReport(true, pages, changes, bootstrapped, false, null, completedAt);
        }
        catch (OperationCanceledException)
        {
            SetStatus(SyncStatusType.Idle, changes, LocalTransportCodes.Cancelled);
            return new SyncReport(false, pages, changes, bootstrapped, false, SyncError.Cancelled, null);
        }
        catch (SyncProtocolException exception)
        {
            SetStatus(SyncStatusType.Failed, changes, exception.Code);
            return new SyncReport(
                false,
                pages,
                changes,
                bootstrapped,
                false,
                new SyncError(SyncErrorKind.Protocol, exception.Code),
                null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never surface message text: it can contain server prose or paths.
            SetStatus(SyncStatusType.Failed, changes, "sync.local_failure");
            return new SyncReport(
                false,
                pages,
                changes,
                bootstrapped,
                false,
                new SyncError(SyncErrorKind.Protocol, "sync.local_failure"),
                null);
        }
    }

    private async Task<(SnapshotBootstrapResult Result, SyncState State)> RunBootstrapAsync(
        CancellationToken cancellationToken)
    {
        var result = await _bootstrap.BootstrapAsync(_store, cancellationToken).ConfigureAwait(false);
        var state = await _store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return (result, state);
    }

    private async Task<SyncReport> FailBootstrapAsync(
        SnapshotBootstrapResult result,
        int pages,
        int changes,
        CancellationToken cancellationToken)
    {
        var (status, freshness, error) = result.Outcome switch
        {
            SnapshotBootstrapOutcome.SnapshotVanished => (
                SyncStatusType.SnapshotVanished,
                ProjectionFreshness.NeedsRestore,
                new SyncError(SyncErrorKind.SnapshotVanished, WireErrorCodes.BackupNotFound)),
            SnapshotBootstrapOutcome.SnapshotTooLarge => (
                SyncStatusType.SnapshotTooLarge,
                ProjectionFreshness.SnapshotTooLarge,
                new SyncError(SyncErrorKind.SnapshotTooLarge, WireErrorCodes.ClientResponseTooLarge)),
            SnapshotBootstrapOutcome.Corrupt => (
                SyncStatusType.NeedsRestore,
                ProjectionFreshness.NeedsRestore,
                new SyncError(SyncErrorKind.Corrupt, WireErrorCodes.BackupCorrupt)),
            _ => (
                SyncStatusType.Failed,
                ProjectionFreshness.Failed,
                result.Error ?? new SyncError(SyncErrorKind.Protocol, "sync.local_failure")),
        };

        // A 401 during bootstrap is a session problem, not a projection
        // problem: without a valid session no later run can succeed, so the UI
        // must offer sign-in instead of a restore action.
        if (error.Kind == SyncErrorKind.SessionExpired)
        {
            status = SyncStatusType.NeedsSignIn;
        }

        await _store
            .RecordAttemptAsync(freshness, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        SetStatus(status, changes, error.Code);

        return new SyncReport(false, pages, changes, false, false, error, null);
    }

    private async Task<SyncReport> FailRestoreAsync(
        int pages,
        int changes,
        bool bootstrapped,
        CancellationToken cancellationToken)
    {
        await _store
            .RecordAttemptAsync(ProjectionFreshness.NeedsRestore, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        SetStatus(SyncStatusType.NeedsRestore, changes, WireErrorCodes.SyncCursorInvalid);

        return new SyncReport(
            false,
            pages,
            changes,
            bootstrapped,
            false,
            new SyncError(SyncErrorKind.Protocol, WireErrorCodes.SyncCursorInvalid),
            null);
    }

    /// <summary>
    /// Server 0.1.1 signals an unusable cursor two ways: 410
    /// <c>sync.cursor_invalid</c> (expired/unknown cursor) and 400
    /// <c>sync.invalid</c> with <c>details.requiresSnapshot=true</c> (malformed
    /// cursor). A plain 400 <c>sync.invalid</c> without that flag is a request
    /// error and must not wipe the local projection.
    /// </summary>
    private static bool IsCursorInvalid(ApiResult<SyncChangesResponseDto> result) =>
        result.ErrorCode == WireErrorCodes.SyncCursorInvalid
        || (result.StatusCode == 400
            && result.ErrorCode == WireErrorCodes.SyncInvalid
            && result.Error?.RequiresSnapshot == true)
        || (result.StatusCode == 410
            && result.ErrorCode is null or WireErrorCodes.SyncInvalid or WireErrorCodes.SyncCursorInvalid);

    private void SetStatus(
        SyncStatusType status,
        int changeCount,
        string? code,
        DateTimeOffset? lastSyncedAt = null)
    {
        lock (_gate)
        {
            _status = new SyncStatusSnapshot(
                status,
                lastSyncedAt ?? _status.LastSyncedAt,
                changeCount,
                code);
        }

        RaiseStatus();
    }

    private void RaiseStatus()
    {
        StatusChanged?.Invoke(this, Status);
    }
}
