using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;

namespace Mosaic.Auth.Wpf.Internal;

internal enum AccountMode
{
    Login,
    Register,
    Forgot,
    Reset,
    Device
}

/// <summary>
/// Drives the account page: optional sign-in, email-code registration, browser (device flow)
/// sign-in, password reset and explicit backup upload/restore. Nothing here runs at startup unless
/// the page is opened, and no state-changing action contacts the network before the user acts.
/// The signed-out login mode is the account-choice screen: browser sign-in, the existing
/// email+password form, offline use, or connecting a self-hosted server.
/// </summary>
internal sealed class AccountViewModel : ObservableObject, IPageController
{
    private const string SyncEnabledId = "sync.enabled";
    private const string SyncIncludeSettingsId = "sync.includeSettings";
    private const string AccountChoiceMadeId = "auth.accountChoiceMade";

    private readonly IAuthApi _auth;
    private readonly ISyncApi _sync;
    private readonly ISettingsApi _settings;
    private readonly IUserDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Action<Uri> _openUri;
    private readonly Action<string> _copyText;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _devicePolling;
    private CancellationTokenSource? _cooldown;
    private AccountMode _mode = AccountMode.Login;
    private bool _isBusy;
    private bool _isError;
    private bool _showServerPanel;
    private bool _offlineChosen;
    private bool _disposed;
    private string? _message;
    private string _email = "";
    private string _password = "";
    private string _code = "";
    private string _confirmPassword = "";
    private string _newPassword = "";
    private string _confirmNewPassword = "";
    private string _serverInput = "";
    private string _deviceCode = "";
    private string _deviceUri = "";
    private string _deviceStatus = "";
    private bool _deviceRunning;
    private int _cooldownSeconds;
    private int _deviceSecondsLeft;
    private Uri? _deviceVerificationUri;
    private bool _syncEnabled = true;
    private bool _includeSettings = true;
    private bool _includeOnUpload = true;
    private bool _loadingSync;
    private string? _snapshotSummary;
    private string? _restoreSummary;
    private readonly ObservableCollection<AuthSessionInfoDto> _sessions = [];
    private bool _sessionsBusy;
    private bool _deleteDialogVisible;
    private bool _deleteUseCode;
    private string _deleteCredential = "";

    public AccountViewModel(
        IAuthApi auth,
        ISyncApi sync,
        ISettingsApi settings,
        IUserDialogService dialogs,
        IUiDispatcher dispatcher,
        IDiagnosticSink diagnostics,
        Action<Uri>? openUri = null,
        Action<string>? copyText = null)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _openUri = openUri ?? DefaultOpenUri;
        _copyText = copyText ?? DefaultCopy;
        Sessions = new(_sessions);

        LoginCommand = Async(LoginAsync);
        RegisterCommand = Async(RegisterAsync);
        SendCodeCommand = Async(SendCodeAsync, () => CooldownSeconds == 0);
        ForgotCommand = Async(ForgotAsync);
        ResetCommand = Async(ResetAsync);
        StartDeviceCommand = Async(StartDeviceAsync);
        CancelDeviceCommand = Async(CancelDeviceAsync);
        SaveServerCommand = Async(SaveServerAsync);
        LogoutCommand = Async(LogoutAsync);
        UploadCommand = Async(UploadAsync, () => CanSync);
        DownloadCommand = Async(DownloadAsync, () => CanSync);
        RefreshLatestCommand = Async(RefreshLatestAsync, () => _auth.State.Status == AuthStatus.SignedIn);
        CopyDeviceCodeCommand = new RelayCommand(CopyDeviceCode, () => DeviceCode.Length > 0);
        OpenDeviceUriCommand = new RelayCommand(OpenDeviceUri, () => _deviceVerificationUri is not null);
        ShowRegisterCommand = new RelayCommand(() => SetMode(AccountMode.Register));
        ShowForgotCommand = new RelayCommand(() => SetMode(AccountMode.Forgot));
        BackToLoginCommand = new RelayCommand(() => SetMode(AccountMode.Login));
        UseOfflineCommand = new RelayCommand(UseOffline);
        ConnectCommand = new RelayCommand(Connect);
        ToggleServerPanelCommand = new RelayCommand(() => ShowServerPanel = !ShowServerPanel);
        RefreshSessionsCommand = Async(RefreshSessionsAsync, () => _auth.State.Status == AuthStatus.SignedIn && !SessionsBusy);
        RevokeSessionCommand = new AsyncRelayCommand<string>(RevokeSessionAsync);
        RevokeAllSessionsCommand = Async(RevokeAllSessionsAsync, () => _auth.State.Status == AuthStatus.SignedIn);
        ShowDeleteDialogCommand = new RelayCommand(ShowDeleteDialog);
        CancelDeleteCommand = new RelayCommand(CancelDelete);
        SendDeleteCodeCommand = Async(SendDeleteCodeAsync, () => CooldownSeconds == 0);
        ConfirmDeleteCommand = Async(ConfirmDeleteAsync, () => DeleteCredential.Trim().Length > 0);

        _auth.StateChanged += OnAuthChanged;
        _sync.StateChanged += OnSyncChanged;
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
        _serverInput = DefaultServerInput;
    }

    public event EventHandler? PasswordsCleared;

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand RegisterCommand { get; }
    public IAsyncRelayCommand SendCodeCommand { get; }
    public IAsyncRelayCommand ForgotCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IAsyncRelayCommand StartDeviceCommand { get; }
    public IAsyncRelayCommand CancelDeviceCommand { get; }
    public IAsyncRelayCommand SaveServerCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand RefreshLatestCommand { get; }
    public IRelayCommand CopyDeviceCodeCommand { get; }
    public IRelayCommand OpenDeviceUriCommand { get; }
    public IRelayCommand ShowRegisterCommand { get; }
    public IRelayCommand ShowForgotCommand { get; }
    public IRelayCommand BackToLoginCommand { get; }
    public IRelayCommand UseOfflineCommand { get; }
    public IRelayCommand ConnectCommand { get; }
    public IRelayCommand ToggleServerPanelCommand { get; }
    public IAsyncRelayCommand RefreshSessionsCommand { get; }
    public IAsyncRelayCommand<string> RevokeSessionCommand { get; }
    public IAsyncRelayCommand RevokeAllSessionsCommand { get; }
    public IRelayCommand ShowDeleteDialogCommand { get; }
    public IRelayCommand CancelDeleteCommand { get; }
    public IAsyncRelayCommand SendDeleteCodeCommand { get; }
    public IAsyncRelayCommand ConfirmDeleteCommand { get; }

    public AccountMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsLoginMode));
            OnPropertyChanged(nameof(IsRegisterMode));
            OnPropertyChanged(nameof(IsForgotMode));
            OnPropertyChanged(nameof(IsResetMode));
            OnPropertyChanged(nameof(IsDeviceMode));
            OnPropertyChanged(nameof(IsFormMode));
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(ModeDescription));
            NotifyCommands();
        }
    }

    public bool IsLoginMode => Mode == AccountMode.Login;
    public bool IsRegisterMode => Mode == AccountMode.Register;
    public bool IsForgotMode => Mode == AccountMode.Forgot;
    public bool IsResetMode => Mode == AccountMode.Reset;
    public bool IsDeviceMode => Mode == AccountMode.Device;
    public bool IsFormMode => Mode != AccountMode.Device;

    /// <summary>The signed-out login mode is the account-choice screen: browser sign-in, the
    /// existing email+password form, offline use or a self-hosted server.</summary>
    public bool ShowAccountChoice => IsSignedOut && !_offlineChosen && Mode == AccountMode.Login;

    /// <summary>Form fields stay disabled while another account op runs.</summary>
    public bool CanUseAccount => !IsBusy && IsSignedOut;

    public bool IsSignedIn => _auth.State.Status == AuthStatus.SignedIn;
    public bool IsSignedOut => !IsSignedIn;
    public bool IsServerConfigured => _auth.State.Server is not null;
    public bool ShowOfflineNote => IsSignedOut && _offlineChosen;
    public bool ShowLoginCard => IsSignedOut && !ShowOfflineNote;

    public string ServerText => _auth.State.Server?.GetLeftPart(UriPartial.Authority) ?? "";

    // A fresh install shows the shipped instance as the suggested address; the value is only
    // typed into the box, saving it (or replacing it) stays the user's call.
    private string DefaultServerInput => IsServerConfigured ? ServerText : AuthValidation.DefaultServerUrl;

    public string ServerDisplay => IsServerConfigured
        ? string.Format(CultureInfo.CurrentCulture, L("Auth.Server.Current", "当前服务器：{0}"), ServerText)
        : L("Auth.Server.None", "尚未连接服务器");

    public string ServerInput
    {
        get => _serverInput;
        set => SetProperty(ref _serverInput, value);
    }

    public bool ShowServerPanel
    {
        get => _showServerPanel;
        set => SetProperty(ref _showServerPanel, value);
    }

    public string AccountEmail => _auth.State.Account?.Email ?? "";
    public string AccountName => _auth.State.Account?.DisplayName is { Length: > 0 } name ? name : AccountEmail;
    public string AccountInitials => string.IsNullOrWhiteSpace(AccountName)
        ? "?"
        : StringInfo.GetNextTextElement(AccountName.Trim().ToUpperInvariant());

    public string ModeTitle => Mode switch
    {
        AccountMode.Register => L("Auth.Register.Title", "注册账户"),
        AccountMode.Forgot => L("Auth.Forgot.Title", "找回密码"),
        AccountMode.Reset => L("Auth.Reset.Title", "重置密码"),
        AccountMode.Device => L("Auth.Device.Title", "在浏览器中登录"),
        _ => L("Auth.Choice.Title", "选择如何开始")
    };

    public string ModeDescription => Mode switch
    {
        AccountMode.Register => L("Auth.Register.Description", "使用邮箱验证码注册；完成后会自动登录。"),
        AccountMode.Forgot => L("Auth.Forgot.Description", "输入注册邮箱，我们会发送 6 位验证码用于重置密码。"),
        AccountMode.Reset => L("Auth.Reset.Description", "输入收到的验证码并设置新密码。"),
        AccountMode.Device => L("Auth.Device.Description", "在浏览器或手机上打开下面的地址，输入授权码完成确认。"),
        _ => L("Auth.Choice.Description", "登录后可备份任务并在其他设备上恢复；不登录也可以完全离线使用。")
    };

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => SetProperty(ref _confirmPassword, value);
    }

    public string NewPassword
    {
        get => _newPassword;
        set => SetProperty(ref _newPassword, value);
    }

    public string ConfirmNewPassword
    {
        get => _confirmNewPassword;
        set => SetProperty(ref _confirmNewPassword, value);
    }

    public string SendCodeLabel => CooldownSeconds > 0
        ? string.Format(CultureInfo.CurrentCulture, L("Auth.Code.Resend", "重新发送（{0} 秒）"), CooldownSeconds)
        : L("Auth.Code.Send", "发送验证码");

    public int CooldownSeconds
    {
        get => _cooldownSeconds;
        private set
        {
            if (!SetProperty(ref _cooldownSeconds, value)) return;
            OnPropertyChanged(nameof(SendCodeLabel));
            NotifyCommands();
        }
    }

    public string DeviceCode
    {
        get => _deviceCode;
        private set
        {
            if (!SetProperty(ref _deviceCode, value)) return;
            CopyDeviceCodeCommand.NotifyCanExecuteChanged();
        }
    }

    public string DeviceUri
    {
        get => _deviceUri;
        private set => SetProperty(ref _deviceUri, value);
    }

    public string DeviceStatus
    {
        get => _deviceStatus;
        private set => SetProperty(ref _deviceStatus, value);
    }

    public bool IsDeviceRunning
    {
        get => _deviceRunning;
        private set => SetProperty(ref _deviceRunning, value);
    }

    public int DeviceSecondsLeft
    {
        get => _deviceSecondsLeft;
        private set
        {
            if (!SetProperty(ref _deviceSecondsLeft, value)) return;
            OnPropertyChanged(nameof(DeviceExpiryText));
        }
    }

    public string DeviceExpiryText => string.Format(
        CultureInfo.CurrentCulture,
        L("Auth.Device.Expires", "授权码剩余有效时间：{0} 秒"),
        Math.Max(0, DeviceSecondsLeft));

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanUseAccount));
            NotifyCommands();
        }
    }

    public bool IsIdle => !IsBusy;

    public string? Message
    {
        get => _message;
        private set
        {
            if (!SetProperty(ref _message, value)) return;
            OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public bool SyncEnabled
    {
        get => _syncEnabled;
        set
        {
            if (!SetProperty(ref _syncEnabled, value) || _loadingSync) return;
            _ = PersistSyncOptionAsync(SyncEnabledId, value);
        }
    }

    public bool IncludeSettings
    {
        get => _includeSettings;
        set
        {
            if (!SetProperty(ref _includeSettings, value) || _loadingSync) return;
            _ = PersistSyncOptionAsync(SyncIncludeSettingsId, value);
        }
    }

    public bool IncludeOnUpload
    {
        get => _includeOnUpload;
        set => SetProperty(ref _includeOnUpload, value);
    }

    public bool CanSync => IsSignedIn && SyncEnabled && !_sync.State.IsBusy;

    public string SnapshotSummary => _snapshotSummary ?? L("Auth.Sync.None", "服务器上还没有备份。");

    public bool HasSnapshot => _sync.State.LatestSnapshot is not null;

    public string? RestoreSummary => _restoreSummary;

    public bool HasRestore => !string.IsNullOrEmpty(RestoreSummary);

    public ReadOnlyObservableCollection<AuthSessionInfoDto> Sessions { get; }

    public bool HasSessions => Sessions.Count > 0;

    public bool ShowSessionList => HasSessions && !SessionsBusy;

    public bool ShowSessionsLoading => !HasSessions && SessionsBusy;

    public bool SessionsBusy
    {
        get => _sessionsBusy;
        private set
        {
            if (!SetProperty(ref _sessionsBusy, value)) return;
            RefreshSessionsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ShowSessionList));
            OnPropertyChanged(nameof(ShowSessionsLoading));
        }
    }

    public bool DeleteDialogVisible
    {
        get => _deleteDialogVisible;
        private set
        {
            if (!SetProperty(ref _deleteDialogVisible, value)) return;
            OnPropertyChanged(nameof(DeleteDialogHidden));
        }
    }

    public bool DeleteDialogHidden => !DeleteDialogVisible;

    public bool DeleteUseCode
    {
        get => _deleteUseCode;
        set
        {
            if (!SetProperty(ref _deleteUseCode, value)) return;
            OnPropertyChanged(nameof(DeleteUsePassword));
            OnPropertyChanged(nameof(DeleteCredentialLabel));
            OnPropertyChanged(nameof(DeleteCredentialPlaceholder));
        }
    }

    public bool DeleteUsePassword
    {
        get => !DeleteUseCode;
        set => DeleteUseCode = !value;
    }

    public string DeleteCredential
    {
        get => _deleteCredential;
        set
        {
            if (!SetProperty(ref _deleteCredential, value)) return;
            ConfirmDeleteCommand.NotifyCanExecuteChanged();
        }
    }

    public string DeleteCredentialLabel => DeleteUseCode
        ? L("Auth.Delete.CodeLabel", "邮箱验证码")
        : L("Auth.Delete.PasswordLabel", "账户密码");

    public string DeleteCredentialPlaceholder => DeleteUseCode
        ? L("Auth.Delete.CodePlaceholder", "输入收到的 6 位验证码")
        : L("Auth.Delete.PasswordPlaceholder", "输入当前账户密码");

    public async Task InitializeAsync()
    {
        await LoadSyncOptionsAsync().ConfigureAwait(true);
        RefreshSummaries();
        if (IsSignedIn && SyncEnabled)
        {
            var latest = await _sync.RefreshLatestAsync(_lifetime.Token).ConfigureAwait(true);
            if (!latest.IsSuccess) SetMessage(AuthLocalization.Error(latest.Error), isError: true);
            RefreshSummaries();
        }
        if (IsSignedIn)
        {
            var sessions = await _auth.ListSessionsAsync(_lifetime.Token).ConfigureAwait(true);
            if (sessions.IsSuccess)
                ReplaceSessions(sessions.Value);
        }
        NotifyAuth();
    }

    public Task<bool> CanLeaveAsync() => Task.FromResult(true);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _auth.StateChanged -= OnAuthChanged;
        _sync.StateChanged -= OnSyncChanged;
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
        _devicePolling?.Cancel();
        _devicePolling?.Dispose();
        _devicePolling = null;
        _cooldown?.Cancel();
        _cooldown?.Dispose();
        _cooldown = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task LoginAsync()
    {
        if (!ValidateEmail() || !ValidatePassword(Password)) return;
        var result = await _auth.LoginAsync(Email.Trim(), Password, _lifetime.Token).ConfigureAwait(true);
        if (!Complete(result, L("Auth.Login.Success", "登录成功。"))) return;
    }

    private async Task SendCodeAsync()
    {
        if (!ValidateEmail()) return;
        var purpose = Mode switch
        {
            AccountMode.Register => AuthEmailCodePurpose.Register,
            AccountMode.Reset => AuthEmailCodePurpose.Reset,
            _ => AuthEmailCodePurpose.Login
        };
        var result = await _auth.RequestEmailCodeAsync(Email.Trim(), purpose, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        StartCooldown();
        SetMessage(L("Auth.Code.Sent", "验证码已发送，请查收邮箱。"), isError: false);
    }

    private async Task RegisterAsync()
    {
        if (!ValidateEmail()) return;
        if (!AuthValidation.IsValidEmailCode(Code))
        {
            Fail(L("Auth.Validation.Code", "请输入 6 位数字验证码。"));
            return;
        }
        if (!ValidatePassword(Password)) return;
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            Fail(L("Auth.Validation.Confirm", "两次输入的密码不一致。"));
            return;
        }
        var result = await _auth.RegisterAsync(Email.Trim(), Code.Trim(), Password, _lifetime.Token).ConfigureAwait(true);
        Complete(result, L("Auth.Register.Success", "注册成功，已登录。"));
    }

    private async Task ForgotAsync()
    {
        if (!ValidateEmail()) return;
        var result = await _auth.ForgotPasswordAsync(Email.Trim(), _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        Code = "";
        SetMode(AccountMode.Reset);
        StartCooldown();
        SetMessage(L("Auth.Code.Sent", "验证码已发送，请查收邮箱。"), isError: false);
    }

    private async Task ResetAsync()
    {
        if (!ValidateEmail()) return;
        if (!AuthValidation.IsValidEmailCode(Code))
        {
            Fail(L("Auth.Validation.Code", "请输入 6 位数字验证码。"));
            return;
        }
        if (!ValidatePassword(NewPassword)) return;
        if (!string.Equals(NewPassword, ConfirmNewPassword, StringComparison.Ordinal))
        {
            Fail(L("Auth.Validation.Confirm", "两次输入的密码不一致。"));
            return;
        }
        var result = await _auth.ResetPasswordAsync(Email.Trim(), Code.Trim(), NewPassword, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        ClearInput();
        SetMode(AccountMode.Login);
        SetMessage(L("Auth.Reset.Success", "密码已重置，请使用新密码登录。"), isError: false);
    }

    private async Task StartDeviceAsync()
    {
        if (!IsServerConfigured)
        {
            ShowServerPanel = true;
            Fail(L("Auth.Error.ServerRequired", "请先填写服务器地址。"));
            return;
        }
        // Starting a sign-in is itself a first-run choice, recorded through the settings journal.
        await MarkAccountChoiceMadeAsync(null).ConfigureAwait(true);
        var started = await _auth.StartDeviceAuthorizationAsync(_lifetime.Token).ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            Fail(AuthLocalization.Error(started.Error));
            return;
        }
        var authorization = started.Value;
        SetMode(AccountMode.Device);
        DeviceCode = FormatUserCode(authorization.UserCode);
        _deviceVerificationUri = authorization.VerificationUriComplete ?? authorization.VerificationUri;
        DeviceUri = _deviceVerificationUri.ToString();
        DeviceStatus = L("Auth.Device.Waiting", "等待浏览器确认…");
        IsDeviceRunning = true;
        OpenDeviceUriCommand.NotifyCanExecuteChanged();
        OpenDeviceUri();
        StartDevicePolling(authorization.IntervalSeconds, authorization.ExpiresInSeconds);
    }

    private Task CancelDeviceAsync()
    {
        StopDevicePolling();
        IsDeviceRunning = false;
        DeviceCode = "";
        DeviceUri = "";
        DeviceStatus = "";
        _deviceVerificationUri = null;
        SetMode(AccountMode.Login);
        return _auth.CancelDeviceAuthorizationAsync();
    }

    private async Task SaveServerAsync()
    {
        var result = await _auth.SetServerAsync(ServerInput.Trim(), _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        ShowServerPanel = false;
        _offlineChosen = false;
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        SetMessage(L("Auth.Server.Saved", "服务器地址已保存。"), isError: false);
    }

    private async Task LogoutAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            L("Auth.Account.Logout", "退出登录"),
            L("Auth.Logout.Confirm", "退出后本机保存的登录凭据会被清除，任务数据仍保留在本机。")).ConfigureAwait(true);
        if (!confirmed) return;
        var result = await _auth.LogoutAsync(_lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        _snapshotSummary = null;
        // Signing out re-arms the account-choice flow: clearing the flag through the
        // settings journal brings the choice back on this page and on the next launch.
        await ClearAccountChoiceAsync().ConfigureAwait(true);
        SetMode(AccountMode.Login);
        SetMessage(L("Auth.Logout.Done", "已退出登录。"), isError: false);
    }

    private async Task UploadAsync()
    {
        var result = await _sync.UploadAsync(IncludeOnUpload, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        RefreshSummaries();
        SetMessage(string.Format(
            CultureInfo.CurrentCulture,
            L("Auth.Sync.Uploaded", "备份已上传：{0} 个任务，{1} 项设置。"),
            result.Value.TaskCount,
            result.Value.SettingCount), isError: false);
    }

    private async Task DownloadAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            L("Auth.Sync.Confirm.Title", "覆盖本机任务"),
            L("Auth.Sync.Confirm.Message", "从服务器下载备份会替换本机全部任务，并可能覆盖可移植设置，且无法撤销。是否继续？")).ConfigureAwait(true);
        if (!confirmed) return;
        var result = await _sync.DownloadAndReplaceAsync(_lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        RefreshSummaries();
        SetMessage(string.Format(
            CultureInfo.CurrentCulture,
            L("Auth.Sync.Restored", "已从备份恢复：{0} 个任务，{1} 项设置。"),
            result.Value.TaskCount,
            result.Value.SettingCount), isError: false);
    }

    private async Task RefreshLatestAsync()
    {
        var latest = await _sync.RefreshLatestAsync(_lifetime.Token).ConfigureAwait(true);
        if (!latest.IsSuccess)
        {
            Fail(AuthLocalization.Error(latest.Error));
            return;
        }
        RefreshSummaries();
    }

    private void ReplaceSessions(IReadOnlyList<AuthSessionInfoDto> sessions)
    {
        _sessions.Clear();
        foreach (var session in sessions) _sessions.Add(session);
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowSessionList));
        OnPropertyChanged(nameof(ShowSessionsLoading));
    }

    private async Task RefreshSessionsAsync()
    {
        SessionsBusy = true;
        try
        {
            var result = await _auth.ListSessionsAsync(_lifetime.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Fail(AuthLocalization.Error(result.Error));
                return;
            }
            ReplaceSessions(result.Value);
        }
        finally { SessionsBusy = false; }
    }

    private async Task RevokeSessionAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var result = await _auth.RevokeSessionAsync(sessionId, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        ReplaceSessions(_sessions.Where(session => session.Id != sessionId).ToList());
        SetMessage(L("Auth.Sessions.Revoked", "会话已撤销。"), isError: false);
    }

    private async Task RevokeAllSessionsAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            L("Auth.Sessions.RevokeOthers.Title", "撤销其他会话"),
            L("Auth.Sessions.RevokeOthers.Message", "除当前设备外，其他所有设备都会被登出，需要重新登录。是否继续？")).ConfigureAwait(true);
        if (!confirmed) return;
        var result = await _auth.RevokeAllSessionsAsync(keepCurrent: true, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        SetMessage(L("Auth.Sessions.RevokeOthers.Done", "其他会话已撤销。"), isError: false);
        await RefreshSessionsAsync().ConfigureAwait(true);
    }

    private void ShowDeleteDialog()
    {
        DeleteCredential = "";
        DeleteUseCode = false;
        DeleteDialogVisible = true;
    }

    private void CancelDelete()
    {
        DeleteCredential = "";
        DeleteDialogVisible = false;
    }

    private async Task SendDeleteCodeAsync()
    {
        var email = AccountEmail;
        if (!AuthValidation.IsValidEmail(email))
        {
            Fail(L("Auth.Validation.Email", "请输入有效的邮箱地址。"));
            return;
        }
        var result = await _auth.RequestEmailCodeAsync(email, AuthEmailCodePurpose.DeleteAccount, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        StartCooldown();
        SetMessage(L("Auth.Code.Sent", "验证码已发送，请查收邮箱。"), isError: false);
    }

    private async Task ConfirmDeleteAsync()
    {
        var credential = DeleteCredential.Trim();
        string? password;
        string? code;
        if (DeleteUseCode)
        {
            if (!AuthValidation.IsValidEmailCode(credential))
            {
                Fail(L("Auth.Validation.Code", "请输入 6 位数字验证码。"));
                return;
            }
            password = null;
            code = credential;
        }
        else
        {
            if (credential.Length == 0)
            {
                Fail(L("Auth.Validation.Password", "密码需为 10–256 个字符。"));
                return;
            }
            password = credential;
            code = null;
        }
        var confirmed = await _dialogs.ConfirmAsync(
            L("Auth.Delete.Confirm.Title", "永久删除账号"),
            L("Auth.Delete.Confirm.Message", "账号与云端全部数据将被永久删除且无法恢复，本机数据保留。是否继续？")).ConfigureAwait(true);
        if (!confirmed) return;
        var result = await _auth.DeleteAccountAsync(password, code, _lifetime.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return;
        }
        CancelDelete();
        SetMessage(L("Auth.Delete.Done", "账号已删除。"), isError: false);
    }

    private async Task PersistSyncOptionAsync(string id, bool value)
    {
        try
        {
            var session = await _settings.BeginEditSessionAsync(_lifetime.Token).ConfigureAwait(true);
            if (!session.IsSuccess)
            {
                Fail(AuthLocalization.Error(session.Error));
                await LoadSyncOptionsAsync().ConfigureAwait(true);
                return;
            }
            await using var edit = session.Value;
            var changes = new Dictionary<string, object?> { [id] = value };
            var preview = await edit.PreviewAsync(changes, 1, _lifetime.Token).ConfigureAwait(true);
            if (!preview.IsSuccess)
            {
                Fail(AuthLocalization.Error(preview.Error));
                await LoadSyncOptionsAsync().ConfigureAwait(true);
                return;
            }
            if (preview.Value.Errors.Count > 0)
            {
                Fail(AuthLocalization.Error(preview.Value.Errors.First().Value));
                await LoadSyncOptionsAsync().ConfigureAwait(true);
                return;
            }
            var commit = await edit.CommitAsync(1, _lifetime.Token).ConfigureAwait(true);
            if (!commit.IsSuccess)
            {
                Fail(AuthLocalization.Error(commit.Error));
                await LoadSyncOptionsAsync().ConfigureAwait(true);
            }
            NotifyCommands();
            OnPropertyChanged(nameof(CanSync));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            Fail(L("Auth.Error.Unknown", "操作未完成，请稍后再试。"));
            await LoadSyncOptionsAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadSyncOptionsAsync()
    {
        var values = await _settings.GetValuesAsync(_lifetime.Token).ConfigureAwait(true);
        if (!values.IsSuccess) return;
        _loadingSync = true;
        try
        {
            SyncEnabled = values.Value.GetValueOrDefault(SyncEnabledId) is not false;
            IncludeSettings = values.Value.GetValueOrDefault(SyncIncludeSettingsId) is not false;
            IncludeOnUpload = IncludeSettings;
        }
        finally { _loadingSync = false; }
        OnPropertyChanged(nameof(CanSync));
        NotifyCommands();
    }

    private void StartDevicePolling(int intervalSeconds, int expiresInSeconds)
    {
        StopDevicePolling();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _devicePolling = source;
        DeviceSecondsLeft = Math.Max(0, expiresInSeconds);
        _ = PollDeviceAsync(Math.Clamp(intervalSeconds, 2, 30), 0, source.Token);
    }

    private async Task PollDeviceAsync(int baseIntervalSeconds, int nextPollAtSeconds, CancellationToken token)
    {
        // Server 0.1.1 answers a too-fast poll with 200 {status:"slow_down", interval}; honour it by
        // adding the hinted interval on top of the current cadence instead of treating it as pending.
        var effectiveInterval = baseIntervalSeconds;
        var slowDownIncrement = 0;
        var pollAt = nextPollAtSeconds;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                OnUi(() => { if (DeviceSecondsLeft > 0) DeviceSecondsLeft--; });
                if (DeviceSecondsLeft <= 0)
                {
                    await StopWithStatusAsync(
                        L("Auth.Device.Expired", "授权码已过期，请重新获取。"), token).ConfigureAwait(false);
                    return;
                }
                if (DeviceSecondsLeft > pollAt) continue;
                var polled = await _auth.PollDeviceAuthorizationAsync(token).ConfigureAwait(false);
                if (!polled.IsSuccess)
                {
                    await StopWithStatusAsync(AuthLocalization.Error(polled.Error), token).ConfigureAwait(false);
                    return;
                }
                pollAt = NextPollAt(DeviceSecondsLeft, effectiveInterval);
                if (polled.Value.SlowDownIntervalSeconds is int slowDown && slowDown > 0)
                {
                    slowDownIncrement += slowDown;
                    effectiveInterval = Math.Clamp(baseIntervalSeconds + slowDownIncrement, baseIntervalSeconds, 30);
                    pollAt = NextPollAt(DeviceSecondsLeft, effectiveInterval);
                }
                switch (polled.Value.Status)
                {
                    case DeviceAuthorizationStatus.Pending:
                        continue;
                    case DeviceAuthorizationStatus.Approved:
                        OnUi(() =>
                        {
                            IsDeviceRunning = false;
                            DeviceStatus = L("Auth.Device.Approved", "已在浏览器中确认，登录成功。");
                            SetMode(AccountMode.Login);
                            SetMessage(L("Auth.Login.Success", "登录成功。"), isError: false);
                        });
                        return;
                    case DeviceAuthorizationStatus.Denied:
                        await StopWithStatusAsync(L("Auth.Device.Denied", "已在浏览器中拒绝本次登录。"), token).ConfigureAwait(false);
                        return;
                    default:
                        await StopWithStatusAsync(L("Auth.Device.Expired", "授权码已过期，请重新获取。"), token).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
        }
    }

    private static int NextPollAt(int secondsLeft, int intervalSeconds) =>
        Math.Max(0, secondsLeft - Math.Max(1, intervalSeconds));

    private Task StopWithStatusAsync(string status, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.CompletedTask;
        OnUi(() =>
        {
            IsDeviceRunning = false;
            DeviceStatus = status;
            DeviceCode = "";
            DeviceUri = "";
            _deviceVerificationUri = null;
            SetMode(AccountMode.Login);
        });
        return Task.CompletedTask;
    }

    private void StopDevicePolling()
    {
        _devicePolling?.Cancel();
        _devicePolling?.Dispose();
        _devicePolling = null;
    }

    private void StartCooldown()
    {
        _cooldown?.Cancel();
        _cooldown?.Dispose();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cooldown = source;
        CooldownSeconds = 60;
        _ = CooldownAsync(source);
    }

    private async Task CooldownAsync(CancellationTokenSource source)
    {
        try
        {
            while (CooldownSeconds > 0 && !source.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), source.Token).ConfigureAwait(false);
                OnUi(() => { if (CooldownSeconds > 0) CooldownSeconds--; });
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void CopyDeviceCode()
    {
        try
        {
            _copyText(DeviceCode);
            SetMessage(L("Auth.Device.Copied", "授权码已复制。"), isError: false);
        }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            Fail(L("Auth.Device.CopyFailed", "无法复制授权码，请手动输入。"));
        }
    }

    private void OpenDeviceUri()
    {
        if (_deviceVerificationUri is null) return;
        try { _openUri(_deviceVerificationUri); }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            Fail(L("Auth.Device.OpenFailed", "无法打开浏览器，请手动访问下方地址。"));
        }
    }

    private void UseOffline()
    {
        _ = MarkAccountChoiceMadeAsync(() =>
        {
            _offlineChosen = true;
            _showServerPanel = false;
            OnPropertyChanged(nameof(ShowServerPanel));
            OnPropertyChanged(nameof(ShowOfflineNote));
            OnPropertyChanged(nameof(ShowLoginCard));
            OnPropertyChanged(nameof(ShowAccountChoice));
            ClearMessage();
        });
    }

    private void Connect()
    {
        _offlineChosen = false;
        SetMode(AccountMode.Login);
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        OnPropertyChanged(nameof(ShowAccountChoice));
    }

    private void SetMode(AccountMode mode)
    {
        if (mode != AccountMode.Device) StopDevicePolling();
        Mode = mode;
        ClearMessage();
    }

    // Records the first-run account choice through a normal settings commit. A merely configured
    // server address does not count; the choice flag exists only because the user acted.
    private async Task MarkAccountChoiceMadeAsync(Action? onDone)
    {
        try
        {
            var session = await _settings.BeginEditSessionAsync().ConfigureAwait(true);
            if (session.IsSuccess && session.Value is { } edit)
            {
                await using (edit.ConfigureAwait(true))
                {
                    var preview = await edit.PreviewAsync(
                        new Dictionary<string, object?> { [AccountChoiceMadeId] = true }, 1, _lifetime.Token).ConfigureAwait(true);
                    if (preview.IsSuccess) await edit.CommitAsync(1, _lifetime.Token).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) { _diagnostics.Report(Guid.NewGuid().ToString("N"), ex); }
        finally { onDone?.Invoke(); }
    }

    /// <summary>Clears the first-run choice flag (a null value removes it) so the
    /// account-choice screen returns after sign-out; mirrors the status item's sign-out
    /// path so both entry points leave the same journal state.</summary>
    private async Task ClearAccountChoiceAsync()
    {
        try
        {
            var session = await _settings.BeginEditSessionAsync().ConfigureAwait(true);
            if (session.IsSuccess && session.Value is { } edit)
            {
                await using (edit.ConfigureAwait(true))
                {
                    var preview = await edit.PreviewAsync(
                        new Dictionary<string, object?> { [AccountChoiceMadeId] = null }, 1, _lifetime.Token).ConfigureAwait(true);
                    if (preview.IsSuccess) await edit.CommitAsync(1, _lifetime.Token).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) { _diagnostics.Report(Guid.NewGuid().ToString("N"), ex); }
    }

    private bool Complete(OperationResult result, string successMessage)
    {
        if (!result.IsSuccess)
        {
            Fail(AuthLocalization.Error(result.Error));
            return false;
        }
        ClearInput();
        PasswordsCleared?.Invoke(this, EventArgs.Empty);
        SetMode(AccountMode.Login);
        SetMessage(successMessage, isError: false);
        return true;
    }

    private bool ValidateEmail()
    {
        if (AuthValidation.IsValidEmail(Email)) return true;
        Fail(L("Auth.Validation.Email", "请输入有效的邮箱地址。"));
        return false;
    }

    private bool ValidatePassword(string password)
    {
        if (AuthValidation.IsValidPassword(password)) return true;
        Fail(L("Auth.Validation.Password", "密码需为 10–256 个字符。"));
        return false;
    }

    private void ClearInput()
    {
        Password = "";
        Code = "";
        ConfirmPassword = "";
        NewPassword = "";
        ConfirmNewPassword = "";
    }

    private void Fail(string message) => SetMessage(message, isError: true);

    private void SetMessage(string? message, bool isError)
    {
        IsError = isError;
        Message = message;
    }

    private void ClearMessage() => SetMessage(null, isError: false);

    private void RefreshSummaries()
    {
        var snapshot = _sync.State.LatestSnapshot;
        _snapshotSummary = snapshot is null
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                L("Auth.Sync.Latest", "最近备份：{0} · {1} 项 · {2}"),
                snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                snapshot.ItemCount,
                FormatBytes(snapshot.TotalBytes));
        var restore = _sync.State.LastRestore;
        _restoreSummary = restore is null
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                L("Auth.Sync.LastRestore", "上次恢复：{0} 个任务，{1} 项设置"),
                restore.TaskCount,
                restore.SettingCount);
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(RestoreSummary));
        OnPropertyChanged(nameof(HasRestore));
        OnPropertyChanged(nameof(CanSync));
        NotifyCommands();
    }

    private void NotifyAuth()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(IsServerConfigured));
        OnPropertyChanged(nameof(ServerText));
        OnPropertyChanged(nameof(ServerDisplay));
        OnPropertyChanged(nameof(CanUseAccount));
        OnPropertyChanged(nameof(AccountEmail));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountInitials));
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        OnPropertyChanged(nameof(ShowAccountChoice));
        OnPropertyChanged(nameof(CanSync));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        LoginCommand.NotifyCanExecuteChanged();
        RegisterCommand.NotifyCanExecuteChanged();
        SendCodeCommand.NotifyCanExecuteChanged();
        ForgotCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        StartDeviceCommand.NotifyCanExecuteChanged();
        CancelDeviceCommand.NotifyCanExecuteChanged();
        SaveServerCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RefreshLatestCommand.NotifyCanExecuteChanged();
        CopyDeviceCodeCommand.NotifyCanExecuteChanged();
        OpenDeviceUriCommand.NotifyCanExecuteChanged();
        RefreshSessionsCommand.NotifyCanExecuteChanged();
        RevokeAllSessionsCommand.NotifyCanExecuteChanged();
        SendDeleteCodeCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCommand.NotifyCanExecuteChanged();
    }

    private void OnAuthChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        NotifyAuth();
        if (IsSignedIn)
        {
            _offlineChosen = false;
            if (Mode == AccountMode.Device) Mode = AccountMode.Login;
        }
    });

    private void OnSyncChanged(object? sender, EventArgs e) => OnUi(RefreshSummaries);

    private void OnLanguageChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        _serverInput = DefaultServerInput;
        OnPropertyChanged(string.Empty);
    });

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _ = _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed) action();
        });
    }

    private AsyncRelayCommand Async(Func<Task> run, Func<bool>? extra = null) =>
        new(() => GuardAsync(run), () => !IsBusy && (extra?.Invoke() ?? true));

    private async Task GuardAsync(Func<Task> run)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        ClearMessage();
        try { await run().ConfigureAwait(true); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            Fail(L("Auth.Error.Unknown", "操作未完成，请稍后再试。"));
        }
        finally { IsBusy = false; }
    }

    private static string FormatUserCode(string code)
    {
        var trimmed = (code ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return trimmed.Length == 8 ? trimmed[..4] + "-" + trimmed[4..] : trimmed;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.0} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024d * 1024d))
    };

    private static string L(string key, string fallback) => AuthLocalization.Localize(key, fallback);

    private static void DefaultOpenUri(Uri uri) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();

    private static void DefaultCopy(string text) => Clipboard.SetText(text);
}
