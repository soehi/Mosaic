using System.Globalization;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.App.Services;

/// <summary>
/// One-time startup: open the database, restore theme/language, restore the
/// session, apply cached settings, then run the first foreground sync. Every
/// page awaits <see cref="InitializeAsync"/> before loading, so no query can
/// run before the schema exists.
/// </summary>
public sealed class StartupService
{
    private readonly MobileDatabase _database;
    private readonly ISyncStore _store;
    private readonly AuthSessionManager _auth;
    private readonly SyncEngine _sync;
    private readonly TaskListViewModel _tasks;
    private readonly AppPreferences _preferences;
    private readonly LocalizationService _localization;
    private readonly AppThemeService _theme;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public StartupService(
        MobileDatabase database,
        ISyncStore store,
        AuthSessionManager auth,
        SyncEngine sync,
        TaskListViewModel tasks,
        AppPreferences preferences,
        LocalizationService localization,
        AppThemeService theme)
    {
        _database = database;
        _store = store;
        _auth = auth;
        _sync = sync;
        _tasks = tasks;
        _preferences = preferences;
        _localization = localization;
        _theme = theme;
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _database.InitializeAsync().ConfigureAwait(true);

            var data = _preferences.Load();
            _localization.Apply(data.Language, CultureInfo.CurrentUICulture.Name);
            _theme.Apply(data.Theme, Application.Current?.RequestedTheme == AppTheme.Dark);

            if (!string.IsNullOrWhiteSpace(data.ServerUrl))
            {
                await _auth.InitializeAsync(data.ServerUrl).ConfigureAwait(true);
            }

            await _sync.InitializeAsync().ConfigureAwait(true);
            await _tasks.LoadAsync().ConfigureAwait(true);

            _auth.StateChanged += OnAuthStateChanged;
            _auth.LocalDataCleared += OnLocalDataCleared;
            _theme.Changed += (_, _) => _preferences.SaveTheme(_theme.Preference);
            _localization.LanguageChanged += (_, _) =>
                _preferences.SaveLanguage(_localization.Preference);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Pause() => _sync.Cancel();

    public async Task ResumeAsync()
    {
        if (!_initialized)
        {
            return;
        }

        if (_auth.State == AuthState.SignedIn)
        {
            await _sync.SyncOnceAsync().ConfigureAwait(true);
        }

        await _tasks.LoadAsync().ConfigureAwait(true);
    }

    private async void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
    {
        try
        {
            _preferences.SaveServerUrl(_auth.ServerUrl);
            if (e.State == AuthState.SignedIn)
            {
                await _sync.SyncOnceAsync().ConfigureAwait(true);
                await _tasks.LoadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // Startup wiring must not crash the app; the sync engine already
            // exposes failures through its status.
        }
    }

    private async void OnLocalDataCleared(object? sender, string reason)
    {
        try
        {
            _sync.Cancel();
            await _store.ClearAllAsync().ConfigureAwait(true);
            await _tasks.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The clear is best effort; tokens are already gone.
        }
    }
}
