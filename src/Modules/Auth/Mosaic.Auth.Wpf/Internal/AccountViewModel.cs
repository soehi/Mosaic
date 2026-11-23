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
    Device
}

/// <summary>
/// Drives the account page: browser (device flow) sign-in, server selection, offline use,
/// browser-based password recovery and explicit backup upload/restore. Registration, the
/// email+password form and password reset live entirely in the browser; the desktop offers
/// exactly four entries. Nothing here runs at startup unless the page is opened, and no
/// state-changing action contacts the network before the user acts.
/// </summary>
internal sealed class AccountViewModel : ObservableObject, IPageController
{
    private const string SyncEnabledId = "sync.enabled";
    private const string SyncIncludeSettingsId = "sync.includeSettings";

    private readonly IAuthApi _auth;
    private readonly ISyncApi _sync;
    private readonly ISettingsApi _settings;
    private readonly IUserDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Action<Uri> _openUri;
    private readonly Action<string> _copyText;

    // 未登录时账户页永远是选择/登录界面：启动页不再被首次运行流程占用，
    // 用户从设置进入账户页时也必须能看到登录、设备码或离线三种入口，而不是空白页。
    public bool ShowStartPage => IsSignedOut;
    public bool ShowAccountSettings => IsSignedIn;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _devicePolling;
    private CancellationTokenSource? _cooldown;
    private AccountMode _mode = AccountMode.Login;
    private bool _isBusy;
    private bool _isError;
    private bool _showServerPanel;
    private bool _offlineChosen;
    private readonly Action<bool>? _offlineChosenChanged;
    private bool _disposed;
    private string? _message;
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
    private readonly SemaphoreSlim _syncPersistGate = new(1, 1);
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
        Action<string>? copyText = null,
        bool offlineChosen = false,
        Action<bool>? offlineChosenChanged = null)
    {
        _offlineChosen = offlineChosen;
        _offlineChosenChanged = offlineChosenChanged;
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _openUri = openUri ?? DefaultOpenUri;
        _copyText = copyText ?? DefaultCopy;
        Sessions = new(_sessions);

        StartDeviceCommand = Async(StartDeviceAsync);
        CancelDeviceCommand = Async(CancelDeviceAsync);
        SaveServerCommand = Async(SaveServerAsync);
        LogoutCommand = Async(LogoutAsync);
        UploadCommand = Async(UploadAsync, () => CanSync);
        DownloadCommand = Async(DownloadAsync, () => CanSync);
        RefreshLatestCommand = Async(RefreshLatestAsync, () => _auth.State.Status == AuthStatus.SignedIn);
        CopyDeviceCodeCommand = new RelayCommand(CopyDeviceCode, () => DeviceCode.Length > 0);
        OpenDeviceUriCommand = new RelayCommand(OpenDeviceUri, () => _deviceVerificationUri is not null);
        ShowForgotCommand = new RelayCommand(OpenForgotPage);
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

    public IAsyncRelayCommand StartDeviceCommand { get; }
    public IAsyncRelayCommand CancelDeviceCommand { get; }
    public IAsyncRelayCommand SaveServerCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand RefreshLatestCommand { get; }
    public IRelayCommand CopyDeviceCodeCommand { get; }
    public IRelayCommand OpenDeviceUriCommand { get; }
    public IRelayCommand ShowForgotCommand { get; }
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
            OnPropertyChanged(nameof(IsDeviceMode));
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(ModeDescription));
            NotifyCommands();
        }
    }

    public bool IsLoginMode => Mode == AccountMode.Login;
    public bool IsDeviceMode => Mode == AccountMode.Device;

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
        AccountMode.Device => L("Auth.Device.Title", "在浏览器中登录"),
        _ => L("Auth.Choice.Title", "选择如何开始")
    };

    public string ModeDescription => Mode switch
    {
        AccountMode.Device => L("Auth.Device.Description", "在浏览器或手机上打开下面的地址，输入授权码完成确认。"),
        _ => L("Auth.Choice.Description", "登录后可备份任务并在其他设备上恢复；不登录也可以完全离线使用。")
    };

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
            QueueSyncOptionsPersist();
        }
    }

    public bool IncludeSettings
    {
        get => _includeSettings;
        set
        {
            if (!SetProperty(ref _includeSettings, value) || _loadingSync) return;
            QueueSyncOptionsPersist();
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

    private async Task StartDeviceAsync()
    {
        if (!IsServerConfigured)
        {
            // Never invent a server on the user's behalf: the shipped default is only a
            // suggestion for the server field, not a destination to sign in against silently.
            ShowServerPanel = true;
            Fail(L("Auth.Error.ServerRequired", "请先填写服务器地址。"));
            return;
        }
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
        SetOfflineChosen(false);
        // Saving an address is configuration, not an account choice: return to the four entries.
        SetMode(AccountMode.Login);
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        SetMessage(L("Auth.Server.Saved", "服务器地址已保存。"), isError: false);
    }

    private async Task LogoutAsync()
    {
        if (IsSignedIn)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                L("Auth.Account.Logout", "退出登录"),
                L("Auth.Logout.Confirm", "退出后本机保存的登录凭据会被清除，任务数据仍保留在本机。"));
            if (!confirmed) return;
            var result = await _auth.LogoutAsync(_lifetime.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Fail(AuthLocalization.Error(result.Error));
                return;
            }
        }
        SetOfflineChosen(false);
        ReplaceSessions([]);
        _snapshotSummary = null;
        _restoreSummary = null;
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(RestoreSummary));
        OnPropertyChanged(nameof(HasRestore));
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        NotifyAuth();
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
                Fail(L("Auth.Validation.PasswordRequired", "请输入当前账户密码。"));
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
        ReplaceSessions([]);
        _snapshotSummary = null;
        _restoreSummary = null;
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(RestoreSummary));
        OnPropertyChanged(nameof(HasRestore));
        NotifyAuth();
        SetMessage(L("Auth.Delete.Done", "账号已删除。"), isError: false);
    }

    // Two quick toggles used to open two edit sessions at once; their previews raced and the second
    // commit could be rejected as stale. Every change is now queued behind one gate and each commit
    // writes the latest values of both switches, so the last state the user sees is the persisted one.
    private void QueueSyncOptionsPersist() => _ = PersistSyncOptionsAsync();

    private async Task PersistSyncOptionsAsync()
    {
        try
        {
            await _syncPersistGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        try
        {
            // Read inside the gate: a queued toggle that arrived while the previous commit ran
            // must be the one written next, not the value captured by its setter.
            var changes = new Dictionary<string, object?>
            {
                [SyncEnabledId] = SyncEnabled,
                [SyncIncludeSettingsId] = IncludeSettings
            };
            var session = await _settings.BeginEditSessionAsync(_lifetime.Token).ConfigureAwait(true);
            if (!session.IsSuccess)
            {
                Fail(AuthLocalization.Error(session.Error));
                await LoadSyncOptionsAsync().ConfigureAwait(true);
                return;
            }
            await using var edit = session.Value;
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
        finally { _syncPersistGate.Release(); }
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
        var expires = Math.Max(0, expiresInSeconds);
        DeviceSecondsLeft = expires;
        // A monotonic deadline owns the countdown; the UI property is a presentation copy. The
        // poller never reads it back, so a background thread cannot race the dispatcher update.
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
            (long)expires * System.Diagnostics.Stopwatch.Frequency;
        // PollDeviceAsync only polls once the countdown has fallen to the next poll mark. Starting
        // that mark at zero would skip every poll until the code had already expired.
        _ = PollDeviceAsync(Math.Clamp(intervalSeconds, 2, 30), deadline, source.Token);
    }

    private async Task PollDeviceAsync(int baseIntervalSeconds, long deadline, CancellationToken token)
    {
        // Server 0.1.1 answers a too-fast poll with 200 {status:"slow_down", interval}; honour it by
        // adding the hinted interval on top of the current cadence instead of treating it as pending.
        var effectiveInterval = baseIntervalSeconds;
        var slowDownIncrement = 0;
        var pollAt = RemainingSeconds(deadline);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                var secondsLeft = RemainingSeconds(deadline);
                OnUi(() => DeviceSecondsLeft = secondsLeft);
                if (secondsLeft <= 0)
                {
                    await StopWithStatusAsync(
                        L("Auth.Device.Expired", "授权码已过期，请重新获取。"), token).ConfigureAwait(false);
                    return;
                }
                if (secondsLeft > pollAt) continue;
                var polled = await _auth.PollDeviceAuthorizationAsync(token).ConfigureAwait(false);
                if (!polled.IsSuccess)
                {
                    await StopWithStatusAsync(AuthLocalization.Error(polled.Error), token).ConfigureAwait(false);
                    return;
                }
                pollAt = NextPollAt(secondsLeft, effectiveInterval);
                if (polled.Value.SlowDownIntervalSeconds is int slowDown && slowDown > 0)
                {
                    slowDownIncrement += slowDown;
                    effectiveInterval = Math.Clamp(baseIntervalSeconds + slowDownIncrement, baseIntervalSeconds, 30);
                    pollAt = NextPollAt(secondsLeft, effectiveInterval);
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

    private static int RemainingSeconds(long deadline) =>
        Math.Max(0, (int)Math.Ceiling((deadline - System.Diagnostics.Stopwatch.GetTimestamp()) /
            (double)System.Diagnostics.Stopwatch.Frequency));

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

    // Password recovery lives in the browser. The desktop only opens the configured server's
    // device page in reset mode (the server renders the reset panel there); it never builds a
    // reset form of its own. Without a configured server the shipped instance is used.
    private void OpenForgotPage()
    {
        var baseUri = _auth.State.Server is { } server
            ? server.GetLeftPart(UriPartial.Authority)
            : AuthValidation.DefaultServerUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUri.TrimEnd('/') + "/device?mode=reset", UriKind.Absolute, out var uri))
        {
            Fail(L("Auth.Error.ServerRequired", "请先填写服务器地址。"));
            return;
        }
        try
        {
            _openUri(uri);
            SetMessage(L("Auth.Forgot.Opened", "已在浏览器中打开密码找回页面，请按页面提示操作。"), isError: false);
        }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            Fail(L("Auth.Device.OpenFailed", "无法打开浏览器，请手动访问服务器地址。"));
        }
    }

    private void UseOffline()
    {
        SetOfflineChosen(true);
        _showServerPanel = false;
        OnPropertyChanged(nameof(ShowServerPanel));
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        ClearMessage();
    }

    private void Connect()
    {
        SetOfflineChosen(false);
        SetMode(AccountMode.Login);
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
    }

    private void SetMode(AccountMode mode)
    {
        if (mode != AccountMode.Device) StopDevicePolling();
        Mode = mode;
        ClearMessage();
    }

    // The page view model is recreated per visit; the factory keeps this one bit so "use offline"
    // survives leaving and returning to the account page, and is cleared by connect/sign-in/logout.
    private void SetOfflineChosen(bool value)
    {
        if (_offlineChosen == value) return;
        _offlineChosen = value;
        _offlineChosenChanged?.Invoke(value);
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
        OnPropertyChanged(nameof(ShowStartPage));
        OnPropertyChanged(nameof(ShowAccountSettings));
        OnPropertyChanged(nameof(IsServerConfigured));
        OnPropertyChanged(nameof(ServerText));
        OnPropertyChanged(nameof(ServerDisplay));
        OnPropertyChanged(nameof(CanUseAccount));
        OnPropertyChanged(nameof(AccountEmail));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountInitials));
        OnPropertyChanged(nameof(ShowOfflineNote));
        OnPropertyChanged(nameof(ShowLoginCard));
        OnPropertyChanged(nameof(CanSync));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
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
            SetOfflineChosen(false);
            if (Mode == AccountMode.Device) SetMode(AccountMode.Login);
            // Sign-in also happens outside this page (device approval elsewhere, token refresh),
            // so sessions and the latest backup are refreshed from the event rather than only in
            // InitializeAsync on first visit.
            _ = RefreshAccountContextAsync();
            return;
        }
        if (Mode == AccountMode.Device) SetMode(AccountMode.Login);
        ReplaceSessions([]);
        _snapshotSummary = null;
        _restoreSummary = null;
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(RestoreSummary));
        OnPropertyChanged(nameof(HasRestore));
        OnPropertyChanged(nameof(CanSync));
        NotifyCommands();
    });

    private async Task RefreshAccountContextAsync()
    {
        try
        {
            var latest = await _sync.RefreshLatestAsync(_lifetime.Token).ConfigureAwait(true);
            if (!latest.IsSuccess && !_lifetime.IsCancellationRequested)
                SetMessage(AuthLocalization.Error(latest.Error), isError: true);
            RefreshSummaries();
            var sessions = await _auth.ListSessionsAsync(_lifetime.Token).ConfigureAwait(true);
            if (sessions.IsSuccess) ReplaceSessions(sessions.Value);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
        }
    }

    private void OnSyncChanged(object? sender, EventArgs e) => OnUi(RefreshSummaries);

    // A language switch re-renders every localized string but must not discard text the user has
    // already typed into the server field (including an unsaved custom address).
    private void OnLanguageChanged(object? sender, EventArgs e) => OnUi(() => OnPropertyChanged(string.Empty));

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
