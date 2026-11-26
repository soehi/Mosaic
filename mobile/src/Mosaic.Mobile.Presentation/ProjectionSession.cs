using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

/// <summary>Why a projection could not be opened for the signed-in account.</summary>
public enum ProjectionSessionFailure
{
    None = 0,

    /// <summary>The file on disk belongs to another account; it was quarantined.</summary>
    IdentityRejected,

    /// <summary>The v1 cache failed validation and was preserved untouched.</summary>
    MigrationRejected,

    /// <summary>The projection file could not be created, opened or read.</summary>
    StorageUnavailable,
}

public sealed record ProjectionOpenResult(bool Opened, ProjectionSessionFailure Failure)
{
    public static ProjectionOpenResult Success { get; } = new(true, ProjectionSessionFailure.None);

    public static ProjectionOpenResult Fail(ProjectionSessionFailure failure) => new(false, failure);
}

/// <summary>
/// Owns the projection and sync engine for exactly one verified account.
///
/// A projection is a rebuildable cache, so it is never global: the app opens it
/// only after a trusted <c>server + user</c> identity exists, and closes it on
/// sign-out, expiry or account switch. There is deliberately no fixed
/// <c>mosaic-mobile.db</c> singleton to fall back to.
///
/// Threading: <see cref="SyncEngine"/> raises its events on whatever thread
/// finished the run. The UI subscribes here, so every forwarded event is
/// marshalled through the captured <see cref="SynchronizationContext"/>.
/// </summary>
public sealed class ProjectionSession : IDisposable
{
    private static readonly SyncStatusSnapshot ClosedStatus =
        new(SyncStatusType.Idle, null, 0, null);

    private readonly IMosaicApi _api;
    private readonly SnapshotBootstrapService _bootstrap;
    private readonly string _appDataDirectory;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();

    private SqliteSyncStore? _store;
    private SyncEngine? _engine;
    private ProjectionIdentity? _identity;
    private long _generation;
    private ProjectionSessionFailure _failure;
    private bool _disposed;

    public ProjectionSession(
        IMosaicApi api,
        SnapshotBootstrapService bootstrap,
        string appDataDirectory,
        TimeProvider? time = null,
        SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _api = api;
        _bootstrap = bootstrap;
        _appDataDirectory = appDataDirectory;
        _time = time ?? TimeProvider.System;
        _ui = uiContext;
    }

    /// <summary>Raised on the UI context with the engine's latest status.</summary>
    public event EventHandler<SyncStatusSnapshot>? StatusChanged;

    /// <summary>Raised on the UI context when a sync run finishes.</summary>
    public event EventHandler<SyncReport>? Completed;

    /// <summary>Raised on the UI context when the open projection changes or closes.</summary>
    public event EventHandler? Changed;

    public ISyncStore? Store
    {
        get
        {
            lock (_stateGate)
            {
                return _store;
            }
        }
    }

    public SyncEngine? Engine
    {
        get
        {
            lock (_stateGate)
            {
                return _engine;
            }
        }
    }

    public ProjectionIdentity? Identity
    {
        get
        {
            lock (_stateGate)
            {
                return _identity;
            }
        }
    }

    public bool IsOpen => Store is not null;

    public ProjectionSessionFailure Failure
    {
        get
        {
            lock (_stateGate)
            {
                return _failure;
            }
        }
    }

    public SyncStatusSnapshot Status
    {
        get
        {
            var engine = Engine;
            return engine?.Status ?? ClosedStatus;
        }
    }

    /// <summary>
    /// Opens (or reuses) the projection for <paramref name="identity"/>.
    /// Opening a different identity closes the previous one first; concurrent
    /// calls are serialised and a superseded open never installs its store.
    /// </summary>
    public async Task<ProjectionOpenResult> OpenAsync(
        ProjectionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_stateGate)
            {
                if (_store is not null && _engine is not null && _identity == identity)
                {
                    return ProjectionOpenResult.Success;
                }

                generation = ++_generation;
            }

            DetachCore();

            SqliteSyncStore store;
            try
            {
                store = await ProjectionStoreFactory
                    .OpenOrCreateAsync(identity, _appDataDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProjectionOwnershipException)
            {
                return Fail(generation, ProjectionSessionFailure.IdentityRejected);
            }
            catch (InvalidOperationException)
            {
                // ProjectionStoreFactory rethrows a rejected v1 migration with
                // the operator-facing reason; the file is preserved.
                return Fail(generation, ProjectionSessionFailure.MigrationRejected);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return Fail(generation, ProjectionSessionFailure.StorageUnavailable);
            }

            var engine = new SyncEngine(_api, store, _bootstrap, _time);
            engine.StatusChanged += OnEngineStatusChanged;
            engine.Completed += OnEngineCompleted;

            try
            {
                await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                engine.StatusChanged -= OnEngineStatusChanged;
                engine.Completed -= OnEngineCompleted;
                throw;
            }
            catch (Exception)
            {
                engine.StatusChanged -= OnEngineStatusChanged;
                engine.Completed -= OnEngineCompleted;
                return Fail(generation, ProjectionSessionFailure.StorageUnavailable);
            }

            lock (_stateGate)
            {
                if (_generation != generation || _disposed)
                {
                    // A newer open/close superseded this one while it was
                    // initialising: never let a stale store win.
                    engine.StatusChanged -= OnEngineStatusChanged;
                    engine.Completed -= OnEngineCompleted;
                    return ProjectionOpenResult.Fail(ProjectionSessionFailure.StorageUnavailable);
                }

                _store = store;
                _engine = engine;
                _identity = identity;
                _failure = ProjectionSessionFailure.None;
            }

            Post(() => Changed?.Invoke(this, EventArgs.Empty));
            RaiseStatus(engine.Status);
            return ProjectionOpenResult.Success;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the projection: cancels any run and drops every handler.</summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                _generation++;
            }

            DetachCore();
        }
        finally
        {
            _gate.Release();
        }

        Post(() => Changed?.Invoke(this, EventArgs.Empty));
        RaiseStatus(ClosedStatus);
    }

    /// <summary>Cancels the in-flight run without closing the projection.</summary>
    public void Cancel() => Engine?.Cancel();

    /// <summary>
    /// Empties the projection rows in place. The owned database file and its
    /// schema stay: deleting the file would leave a live store pointing at an
    /// uninitialised database (see <c>SqliteSyncStore.ClearAllAsync</c>).
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var store = Store;
        if (store is null)
        {
            return;
        }

        await store.ClearAllAsync(cancellationToken).ConfigureAwait(false);
        Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_stateGate)
        {
            _generation++;
        }

        DetachCore();
        _gate.Dispose();
    }

    private ProjectionOpenResult Fail(long generation, ProjectionSessionFailure failure)
    {
        lock (_stateGate)
        {
            if (_generation != generation)
            {
                return ProjectionOpenResult.Fail(failure);
            }

            _failure = failure;
        }

        RaiseStatus(ClosedStatus);
        return ProjectionOpenResult.Fail(failure);
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void DetachCore()
    {
        SyncEngine? engine;
        lock (_stateGate)
        {
            engine = _engine;
            _store = null;
            _engine = null;
            _identity = null;
        }

        if (engine is null)
        {
            return;
        }

        engine.StatusChanged -= OnEngineStatusChanged;
        engine.Completed -= OnEngineCompleted;
        engine.Cancel();
    }

    private void OnEngineStatusChanged(object? sender, SyncStatusSnapshot status)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _engine))
            {
                return;
            }
        }

        Post(() => StatusChanged?.Invoke(this, status));
    }

    private void OnEngineCompleted(object? sender, SyncReport report)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _engine))
            {
                return;
            }
        }

        Post(() => Completed?.Invoke(this, report));
    }

    private void RaiseStatus(SyncStatusSnapshot status) => Post(() => StatusChanged?.Invoke(this, status));

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        _ui.Post(static state => ((Action)state!)(), action);
    }
}
