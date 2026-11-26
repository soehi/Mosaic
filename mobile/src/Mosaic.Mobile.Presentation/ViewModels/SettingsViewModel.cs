using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

/// <summary>
/// Appearance and language are device preferences; everything else in this page
/// reflects the projection owned by <see cref="ProjectionSession"/>. There is no
/// write path in phase one, so the only actions are a foreground sync and a
/// preference change.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AuthSessionManager _auth;
    private readonly ProjectionSession _session;
    private readonly AppThemeService _theme;
    private readonly Func<bool> _systemDark;
    private readonly Func<string> _systemLanguage;
    private int _cachedTaskCount;

    public SettingsViewModel(
        AuthSessionManager auth,
        ProjectionSession session,
        AppThemeService theme,
        LocalizationService localizer,
        Func<bool>? systemDark = null,
        Func<string>? systemLanguage = null)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(theme);
        _auth = auth;
        _session = session;
        _theme = theme;
        _systemDark = systemDark ?? (() => false);
        _systemLanguage = systemLanguage ?? (() => "zh-Hans");

        _auth.StateChanged += OnAuthChanged;
        _session.StatusChanged += OnSyncStatusChanged;
        _session.Changed += OnProjectionChanged;

        ThemeChoice = theme.Preference;
        LanguageChoice = localizer.Preference;
    }

    [ObservableProperty]
    private string serverText = string.Empty;

    [ObservableProperty]
    private string accountText = string.Empty;

    [ObservableProperty]
    private string versionText = string.Empty;

    [ObservableProperty]
    private string lastSyncedText = string.Empty;

    [ObservableProperty]
    private string cachedTasksText = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private ThemePreference themeChoice;

    [ObservableProperty]
    private AppLanguagePreference languageChoice;

    public string ThemeTitle => T(StringKeys.Theme);

    public string ThemeFollowText => T(StringKeys.ThemeFollowSystem);

    public string ThemeLightText => T(StringKeys.ThemeLight);

    public string TitleText => T(StringKeys.Settings);

    public string ThemeDarkText => T(StringKeys.ThemeDark);

    public string LanguageTitle => T(StringKeys.Language);

    public string LanguageFollowText => T(StringKeys.LanguageFollowSystem);

    public string LanguageChineseText => T(StringKeys.LanguageChinese);

    public string LanguageEnglishText => T(StringKeys.LanguageEnglish);

    public string ServerLabel => T(StringKeys.Server);

    public string AccountLabel => T(StringKeys.Account);

    public string AboutTitle => T(StringKeys.About);

    public string VersionLabel => T(StringKeys.Version);

    public string AppDescriptionText => T(StringKeys.AppDescription);

    public string SyncNowText => T(StringKeys.SyncNow);

    public string LocalHintText => T(StringKeys.LocalPreferenceHint);

    [RelayCommand]
    public async Task LoadAsync()
    {
        ServerText = string.IsNullOrWhiteSpace(_auth.ServerUrl) ? "—" : _auth.ServerUrl;
        AccountText = _auth.AccountEmail ?? T(StringKeys.AccountSignedOut);

        // The projection is per account and can be closed at any moment; read the
        // store once and fall back to an empty count instead of holding a stale one.
        var store = _session.Store;
        _cachedTaskCount = store is null
            ? 0
            : await store.CountTasksAsync().ConfigureAwait(true);

        CachedTasksText = F(StringKeys.CachedTasks, _cachedTaskCount);
        LastSyncedText = SyncStatusText.LastSynced(Localizer, _session.Status.LastSyncedAt);
        NoticeText = ProjectionStatusText.Describe(Localizer, _session.Failure);
        ThemeChoice = _theme.Preference;
        LanguageChoice = Localizer.Preference;
    }

    public void SetVersion(string version) => VersionText = version;

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ThemeTitle));
        OnPropertyChanged(nameof(ThemeFollowText));
        OnPropertyChanged(nameof(ThemeLightText));
        OnPropertyChanged(nameof(ThemeDarkText));
        OnPropertyChanged(nameof(LanguageTitle));
        OnPropertyChanged(nameof(LanguageFollowText));
        OnPropertyChanged(nameof(LanguageChineseText));
        OnPropertyChanged(nameof(LanguageEnglishText));
        OnPropertyChanged(nameof(ServerLabel));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(AboutTitle));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(AppDescriptionText));
        OnPropertyChanged(nameof(SyncNowText));
        OnPropertyChanged(nameof(LocalHintText));
        AccountText = _auth.AccountEmail ?? T(StringKeys.AccountSignedOut);
        CachedTasksText = F(StringKeys.CachedTasks, _cachedTaskCount);
        NoticeText = ProjectionStatusText.Describe(Localizer, _session.Failure);
    }

    [RelayCommand]
    private void SetTheme(string? choice)
    {
        if (!Enum.TryParse<ThemePreference>(choice, ignoreCase: true, out var preference))
        {
            return;
        }

        ThemeChoice = preference;
        _theme.Apply(preference, _systemDark());
    }

    [RelayCommand]
    private void SetLanguage(string? choice)
    {
        if (!Enum.TryParse<AppLanguagePreference>(choice, ignoreCase: true, out var preference))
        {
            return;
        }

        LanguageChoice = preference;
        Localizer.Apply(preference, _systemLanguage());
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        ErrorText = string.Empty;
        NoticeText = string.Empty;
        var engine = _session.Engine;
        if (_auth.State != AuthState.SignedIn || engine is null)
        {
            ErrorText = T(StringKeys.SyncNeedsSignIn);
            return;
        }

        IsBusy = true;
        try
        {
            var report = await engine.SyncOnceAsync().ConfigureAwait(true);
            if (!report.Succeeded && report.Error is { } error)
            {
                ErrorText = error.Kind switch
                {
                    SyncErrorKind.Offline => T(StringKeys.SyncOffline),
                    SyncErrorKind.SessionExpired => T(StringKeys.SyncNeedsSignIn),
                    _ => T(StringKeys.SyncFailed),
                };
                return;
            }

            NoticeText = SyncStatusText.Describe(Localizer, _session.Status);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void Dispose()
    {
        _auth.StateChanged -= OnAuthChanged;
        _session.StatusChanged -= OnSyncStatusChanged;
        _session.Changed -= OnProjectionChanged;
        base.Dispose();
    }

    private void OnProjectionChanged(object? sender, EventArgs e)
    {
        // Opening, switching or closing the projection changes the cached count
        // and the failure notice, so re-read both.
        _ = LoadAsync();
    }

    private void OnAuthChanged(object? sender, AuthStateChangedEventArgs e)
    {
        AccountText = _auth.AccountEmail ?? T(StringKeys.AccountSignedOut);
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusSnapshot e)
    {
        LastSyncedText = SyncStatusText.LastSynced(Localizer, e.LastSyncedAt);
    }
}
