using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

/// <summary>
/// Appearance and language are device preferences; everything else in this page
/// reflects what the server last sent. There is no write path in phase one.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AuthSessionManager _auth;
    private readonly PortableSettings _settings;
    private readonly SyncEngine _sync;
    private readonly AppThemeService _theme;
    private readonly BackupRestoreService _backups;
    private readonly ISyncStore _store;
    private readonly Func<bool> _systemDark;
    private readonly Func<string> _systemLanguage;

    public SettingsViewModel(
        AuthSessionManager auth,
        PortableSettings settings,
        SyncEngine sync,
        AppThemeService theme,
        BackupRestoreService backups,
        ISyncStore store,
        LocalizationService localizer,
        Func<bool>? systemDark = null,
        Func<string>? systemLanguage = null)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(store);
        _auth = auth;
        _settings = settings;
        _sync = sync;
        _theme = theme;
        _backups = backups;
        _store = store;
        _systemDark = systemDark ?? (() => false);
        _systemLanguage = systemLanguage ?? (() => "zh-Hans");

        _settings.Changed += OnSettingsChanged;
        _auth.StateChanged += OnAuthChanged;
        _sync.StatusChanged += OnSyncStatusChanged;

        ThemeChoice = settings.Theme;
        LanguageChoice = settings.Language;
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
    private bool highContrastNoticeVisible;

    [ObservableProperty]
    private bool rebuildConfirmVisible;

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

    public string HighContrastText => T(StringKeys.HighContrastDowngraded);

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

    public string RebuildText => T(StringKeys.RebuildFromSnapshot);

    public string RebuildConfirmText => T(StringKeys.RebuildConfirm);

    public string ConfirmText => T(StringKeys.Confirm);

    public string CancelText => T(StringKeys.Cancel);

    public string LocalHintText => T(StringKeys.LocalPreferenceHint);

    [RelayCommand]
    public async Task LoadAsync()
    {
        ServerText = string.IsNullOrWhiteSpace(_auth.ServerUrl) ? "—" : _auth.ServerUrl;
        AccountText = _auth.AccountEmail ?? T(StringKeys.AccountSignedOut);
        LastSyncedText = SyncStatusText.LastSynced(Localizer, _sync.Status.LastSyncedAt);
        CachedTasksText = F(StringKeys.CachedTasks, await _store.CountTasksAsync().ConfigureAwait(true));
        HighContrastNoticeVisible = _settings.HighContrastDowngraded;
        ThemeChoice = _settings.Theme;
        LanguageChoice = _settings.Language;
    }

    public void SetVersion(string version) => VersionText = version;

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ThemeTitle));
        OnPropertyChanged(nameof(ThemeFollowText));
        OnPropertyChanged(nameof(ThemeLightText));
        OnPropertyChanged(nameof(ThemeDarkText));
        OnPropertyChanged(nameof(HighContrastText));
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
        OnPropertyChanged(nameof(RebuildText));
        OnPropertyChanged(nameof(RebuildConfirmText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(LocalHintText));
        AccountText = _auth.AccountEmail ?? T(StringKeys.AccountSignedOut);
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
        if (_auth.State != AuthState.SignedIn)
        {
            ErrorText = T(StringKeys.SyncNeedsSignIn);
            return;
        }

        IsBusy = true;
        try
        {
            var report = await _sync.SyncOnceAsync().ConfigureAwait(true);
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

            NoticeText = T(StringKeys.SyncSucceeded);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginRebuild()
    {
        if (_auth.State != AuthState.SignedIn)
        {
            ErrorText = T(StringKeys.SyncNeedsSignIn);
            return;
        }

        ErrorText = string.Empty;
        RebuildConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelRebuild() => RebuildConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmRebuildAsync()
    {
        RebuildConfirmVisible = false;
        IsBusy = true;
        try
        {
            var restore = await _backups.RestoreLatestAsync(_store).ConfigureAwait(true);
            if (!restore.Restored)
            {
                ErrorText = T(StringKeys.SyncFailed);
                return;
            }

            NoticeText = T(StringKeys.RebuildDone);
            await _sync.SyncOnceAsync().ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _auth.StateChanged -= OnAuthChanged;
        _sync.StatusChanged -= OnSyncStatusChanged;
        base.Dispose();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        HighContrastNoticeVisible = _settings.HighContrastDowngraded;
        ThemeChoice = _settings.Theme;
        LanguageChoice = _settings.Language;
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
