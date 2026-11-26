using System.Globalization;
using System.Threading.Channels;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Storage;

namespace Mosaic.Mobile.App.Services;

/// <summary>
/// Startup wiring: restore device preferences, restore the session, then open the
/// projection for the *verified* account and run a foreground sync.
///
/// There is deliberately no fixed database here: <see cref="ProjectionSession"/>
/// owns one projection per <c>server + user</c> identity and refuses to open
/// without one. Every page awaits <see cref="InitializeAsync"/> before it loads,
/// so no query can run against a projection that was never opened.
///
/// Auth events are appended to one ordered, serial queue. Every
/// <c>LocalDataCleared</c> is published by <see cref="AuthSessionManager"/>
/// before its matching <c>StateChanged</c>, so the outgoing account's
/// projection is erased and closed before the incoming one is opened. A
/// refresh cannot race a switch and clear the new account's store.
/// </summary>
public sealed class StartupService
{
    private readonly ProjectionSession _session;
    private readonly AuthSessionManager _auth;
    private readonly TaskListViewModel _tasks;
    private readonly AppPreferences _preferences;
    private readonly LocalizationService _localization;
    private readonly AppThemeService _theme;
    private readonly SynchronizationContext? _ui;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly Channel<Func<Task>> _events = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private int _draining;
    private string? _openedOwnerKey;
    private bool _initialized;

    public StartupService(
        ProjectionSession session,
        AuthSessionManager auth,
        TaskListViewModel tasks,
        AppPreferences preferences,
        LocalizationService localization,
        AppThemeService theme,
        SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(theme);

        _session = session;
        _auth = auth;
        _tasks = tasks;
        _preferences = preferences;
        _localization = localization;
        _theme = theme;
        _ui = uiContext;
    }

    public async Task InitializeAsync()
    {
        await _initGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            var data = _preferences.Load();
            _localization.Apply(data.Language, CultureInfo.CurrentUICulture.Name);
            _theme.Apply(data.Theme, Application.Current?.RequestedTheme == AppTheme.Dark);

            // Restoring the session raises StateChanged from inside
            // InitializeAsync, so the handlers are attached only once the restored
            // state is final; the first reconciliation below covers it.
            if (!string.IsNullOrWhiteSpace(data.ServerUrl))
            {
                await _auth.InitializeAsync(data.ServerUrl).ConfigureAwait(true);
            }

            _auth.StateChanged += OnAuthStateChanged;
            _auth.LocalDataCleared += OnLocalDataCleared;
            _theme.Changed += OnThemeChanged;
            _localization.LanguageChanged += OnLanguageChanged;

            await ReconcileAsync().ConfigureAwait(true);
            await _tasks.LoadAsync().ConfigureAwait(true);

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>Cancels the in-flight run when the app leaves the foreground.</summary>
    public void Pause() => _session.Cancel();

    public async Task ResumeAsync()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            await ReconcileAsync().ConfigureAwait(true);
            await _tasks.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Startup wiring must never crash the app; the projection notice and
            // the sync status already convey what failed.
        }
    }

    /// <summary>
    /// Opens or closes the projection so it matches the auth manager right now.
    /// A freshly opened projection is synced once, which is also what bootstraps
    /// an empty projection after the first sign-in.
    /// </summary>
    private async Task ReconcileAsync(bool forceSync = false)
    {
        await _reconcileGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_auth.State != AuthState.SignedIn)
            {
                await CloseAsync().ConfigureAwait(true);
                return;
            }

            var userId = await TrustedUserIdAsync().ConfigureAwait(true);
            if (userId is null)
            {
                // Signed in without a usable server, no user id, or an identity
                // the server rejected: there is no safe projection to open.
                await CloseAsync().ConfigureAwait(true);
                return;
            }

            var identity = TryCreateIdentity(userId, out var ownerKey);
            if (identity is null)
            {
                await CloseAsync().ConfigureAwait(true);
                return;
            }

            var result = await _session.OpenAsync(identity).ConfigureAwait(true);
            if (!result.Opened)
            {
                // ProjectionSession records the reason; the view models render it.
                _openedOwnerKey = null;
                return;
            }

            if (!forceSync && string.Equals(_openedOwnerKey, ownerKey, StringComparison.Ordinal))
            {
                // Same account, already open (a token refresh raises SignedIn
                // again): opening is idempotent, so skip the redundant sync.
                return;
            }

            _openedOwnerKey = ownerKey;
            if (_session.Engine is { } engine)
            {
                await engine.SyncOnceAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// A projection file may only be keyed on an id confirmed by
    /// <c>GET v1/auth/me</c>. When the server cannot be reached, the encrypted
    /// stored session is the fallback; a rejected identity closes the cache.
    /// </summary>
    private async Task<string?> TrustedUserIdAsync()
    {
        var verified = _auth.VerifiedUserId;
        if (!string.IsNullOrWhiteSpace(verified))
        {
            return verified;
        }

        var result = await _auth.VerifyIdentityAsync().ConfigureAwait(true);
        return result.Status switch
        {
            IdentityVerificationStatus.Verified => result.UserId,
            IdentityVerificationStatus.Rejected => null,
            _ => _auth.UserId,
        };
    }

    private async Task CloseAsync()
    {
        _openedOwnerKey = null;
        await _session.CloseAsync().ConfigureAwait(true);
    }

    private ProjectionIdentity? TryCreateIdentity(string userId, out string ownerKey)
    {
        ownerKey = string.Empty;
        var serverUrl = _auth.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        try
        {
            var identity = ProjectionIdentity.Create(serverUrl, userId);
            ownerKey = identity.OwnerKey;
            return identity;
        }
        catch (ArgumentException)
        {
            // The stored server address is not a usable origin (or the user id is
            // malformed); projection files are never keyed on a guess.
            return null;
        }
    }

    private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
    {
        _preferences.SaveServerUrl(_auth.ServerUrl);
        Enqueue(ReconcileAfterAuthChangeAsync);
    }

    private async Task ReconcileAfterAuthChangeAsync()
    {
        try
        {
            await ReconcileAsync().ConfigureAwait(true);
            await _tasks.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Never let a state-change handler take the app down.
        }
    }

    private void OnLocalDataCleared(object? sender, string reason) =>
        Enqueue(ClearAfterSignOutAsync);

    private async Task ClearAfterSignOutAsync()
    {
        try
        {
            // The matching auth event is always published first, so the store
            // still belongs to the account that is leaving. Clearing after a
            // reconcile would instead erase the incoming account projection.
            var store = _session.Store;
            _session.Cancel();
            if (store is not null)
            {
                await store.ClearAllAsync().ConfigureAwait(true);
            }

            await CloseAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The clear is best effort; tokens are already gone.
        }
    }

    private void Enqueue(Func<Task> work)
    {
        _events.Writer.TryWrite(work);
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            return;
        }

        _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_events.Reader.TryRead(out var work))
            {
                // A refresh can publish StateChanged off the UI thread. Marshal
                // each handler so ObservableCollection updates stay on the UI
                // thread even when the queue was started from a worker.
                await OnUiAsync(work).ConfigureAwait(true);
            }
        }
        finally
        {
            Volatile.Write(ref _draining, 0);

            // A producer may have enqueued after the last TryRead and before the
            // flag was released; restart so that work is not stranded. ChannelReader
            // has no IsEmpty member — peek once; a losing race only costs one
            // extra drain pass that finds nothing.
            if (_events.Reader.TryPeek(out _)
                && Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            {
                _ = DrainAsync();
            }
        }
    }

    private async Task OnUiAsync(Func<Task> work)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            await work().ConfigureAwait(true);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try
            {
                await work().ConfigureAwait(true);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, null);
        await completion.Task.ConfigureAwait(true);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _preferences.SaveTheme(_theme.Preference);

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        _preferences.SaveLanguage(_localization.Preference);
}
