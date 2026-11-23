using Mosaic.Auth.Contracts;
using Mosaic.Auth.Wpf.Internal;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;

namespace Mosaic.Wpf.Tests;

// The account page is the only place that knows about authentication. These tests pin the
// behaviour the shell relies on: offline by default, no network without a server, the four
// browser-first entries (no desktop registration/password/reset forms), the browser code
// format, and confirmation before a destructive restore.
public sealed class AccountViewModelTests
{
    private static readonly Uri Server = new("https://sync.example.com/");
    private static readonly AuthAccountDto Account = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户");

    private sealed class FakeAuth : IAuthApi
    {
        public AuthState State { get; set; } = AuthState.SignedOut();
        public Guid DeviceId { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private EventHandler? _stateChanged;
        public event EventHandler? StateChanged { add => _stateChanged += value; remove => _stateChanged -= value; }
        public void RaiseStateChanged() => _stateChanged?.Invoke(this, EventArgs.Empty);
        public int LoginCalls, RegisterCalls, SendCodeCalls, StartDeviceCalls, CancelDeviceCalls, LogoutCalls;
        public int ListSessionsCalls, DeleteAccountCalls;
        public string? LastEmail, LastCode, LastPassword, LastServerInput;
        public string? LastDeletePassword, LastDeleteCode;
        public OperationResult LoginResult = OperationResult.Success();
        public OperationResult RegisterResult = OperationResult.Success();
        public OperationResult SendCodeResult = OperationResult.Success();
        public OperationResult DeleteResult = OperationResult.Success();
        public List<AuthSessionInfoDto> SessionsResult { get; set; } = [];
        public DeviceAuthorizationDto DeviceAuthorization { get; set; } = new(
            "device-code", "BAADC832", new Uri("https://sync.example.com/device"), new Uri("https://sync.example.com/device?code=BAADC832"), 600, 5);
        public Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<AuthPolicyDto>.Success(new(true, true, "Mosaic")));
        public Task<OperationResult> SetServerAsync(string serverUrl, CancellationToken cancellationToken = default)
        { LastServerInput = serverUrl; State = AuthState.SignedOut(new Uri(serverUrl)); return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> RequestEmailCodeAsync(string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default)
        { SendCodeCalls++; LastEmail = email; return Task.FromResult(SendCodeResult); }
        public Task<OperationResult> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default)
        { RegisterCalls++; LastEmail = email; LastCode = code; LastPassword = password; return Task.FromResult(RegisterResult); }
        public Task<OperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        { LoginCalls++; LastEmail = email; LastPassword = password; return Task.FromResult(LoginResult); }
        public Task<OperationResult> LoginWithCodeAsync(string email, string code, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(CancellationToken cancellationToken = default)
        { StartDeviceCalls++; return Task.FromResult(OperationResult<DeviceAuthorizationDto>.Success(DeviceAuthorization)); }
        public DeviceAuthorizationStatus PollStatus { get; set; } = DeviceAuthorizationStatus.Pending;
        public Task<OperationResult<DevicePollDto>> PollDeviceAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            // 真实 AuthCore 在轮询到 approved 时写入登录态并通知订阅者；替身必须做同样的事，
            // 否则账户页只能看到“轮询结束”而永远等不到 IsSignedIn。
            if (PollStatus == DeviceAuthorizationStatus.Approved)
            {
                State = new AuthState(AuthStatus.SignedIn, Account, State.Server);
                RaiseStateChanged();
            }
            return Task.FromResult(OperationResult<DevicePollDto>.Success(new(
                PollStatus,
                PollStatus == DeviceAuthorizationStatus.Approved ? new AuthSessionDto("access", "refresh", 3600, Account) : null)));
        }
        public Task<OperationResult> CancelDeviceAuthorizationAsync() { CancelDeviceCalls++; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> LogoutAsync(CancellationToken cancellationToken = default)
        { LogoutCalls++; State = AuthState.SignedOut(State.Server); return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<string>.Success("access"));
        public Task<OperationResult<IReadOnlyList<AuthSessionInfoDto>>> ListSessionsAsync(CancellationToken cancellationToken = default)
        { ListSessionsCalls++; return Task.FromResult(OperationResult<IReadOnlyList<AuthSessionInfoDto>>.Success(SessionsResult)); }
        public Task<OperationResult> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        { SessionsResult = SessionsResult.Where(session => session.Id != sessionId).ToList(); return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> RevokeAllSessionsAsync(bool keepCurrent, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DeleteAccountAsync(string? password, string? code, CancellationToken cancellationToken = default)
        { DeleteAccountCalls++; LastDeletePassword = password; LastDeleteCode = code; return Task.FromResult(DeleteResult); }
    }

    private sealed class FakeSync : ISyncApi
    {
        public SyncState State { get; set; } = new(false, false, null, null, null);
        public event EventHandler? StateChanged { add { } remove { } }
        public int UploadCalls, DownloadCalls, RefreshCalls;
        public bool? LastIncludeSettings;
        public Task<OperationResult<BackupSnapshotDto?>> RefreshLatestAsync(CancellationToken cancellationToken = default)
        { RefreshCalls++; return Task.FromResult(OperationResult<BackupSnapshotDto?>.Success(State.LatestSnapshot)); }
        public Task<OperationResult<BackupUploadResult>> UploadAsync(bool includeSettings, CancellationToken cancellationToken = default)
        { UploadCalls++; LastIncludeSettings = includeSettings; return Task.FromResult(OperationResult<BackupUploadResult>.Success(new("snap-1", 2, 1, 100))); }
        public Task<OperationResult<RestoreSummary>> DownloadAndReplaceAsync(CancellationToken cancellationToken = default)
        { DownloadCalls++; State = State with { LastRestore = new(3, 2) }; return Task.FromResult(OperationResult<RestoreSummary>.Success(new(3, 2))); }
    }

    private sealed class FakeSettings : ISettingsApi
    {
        public Dictionary<string, object?> Values { get; } = [];
        public List<IReadOnlyDictionary<string, object?>> Commits { get; } = [];
        public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success([]));
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values)));
        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
            IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(new Dictionary<string, OperationError>()));
        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<ISettingsEditSession>> BeginEditSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<ISettingsEditSession>.Success(new Session(this)));
        private sealed class Session(FakeSettings owner) : ISettingsEditSession
        {
            private IReadOnlyDictionary<string, object?> _draft = new Dictionary<string, object?>();
            public IReadOnlyDictionary<string, object?> SavedValues => new Dictionary<string, object?>(owner.Values);
            public Task<OperationResult<SettingPreviewResult>> PreviewAsync(IReadOnlyDictionary<string, object?> changes, long revision, CancellationToken cancellationToken = default)
            { _draft = new Dictionary<string, object?>(changes); return Task.FromResult(OperationResult<SettingPreviewResult>.Success(new(revision, new Dictionary<string, OperationError>(), new Dictionary<string, OperationError>(), changes.Keys.ToArray(), []))); }
            public Task<OperationResult<SettingsCommitResult>> CommitAsync(long expectedRevision, CancellationToken cancellationToken = default)
            {
                owner.Commits.Add(_draft);
                foreach (var item in _draft) owner.Values[item.Key] = item.Value;
                return Task.FromResult(OperationResult<SettingsCommitResult>.Success(new(new Dictionary<string, OperationError>())));
            }
            public Task<OperationResult> DiscardAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Dialogs : IUserDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public int Confirms;
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(ConfirmResult);
        public Task<bool> ConfirmAsync(string title, string message) { Confirms++; return Task.FromResult(ConfirmResult); }
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(UnsavedChoice.Cancel);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private sealed class Dispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }

    private sealed class Diagnostics : IDiagnosticSink
    {
        public List<Exception> Reports { get; } = [];
        public void Report(string correlationId, Exception exception) => Reports.Add(exception);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeAuth Auth { get; } = new();
        public FakeSync Sync { get; } = new();
        public FakeSettings Settings { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public Diagnostics Diagnostics { get; } = new();
        public List<Uri> Opened { get; } = [];
        public List<string> Copied { get; } = [];
        public AccountViewModel ViewModel { get; private set; } = null!;

        public Task<Fixture> StartAsync(bool signedIn = false)
        {
            Auth.State = signedIn ? new AuthState(AuthStatus.SignedIn, Account, Server) : AuthState.SignedOut(Server);
            Sync.State = new(signedIn, false, null, null, null);
            ViewModel = new AccountViewModel(Auth, Sync, Settings, Dialogs, new Dispatcher(), Diagnostics,
                Opened.Add, Copied.Add);
            return Task.FromResult(this);
        }

        public ValueTask DisposeAsync() => ViewModel.DisposeAsync();
    }

    private static Task Run(Func<Task> test) => Sta.Run(test);

    private static Task RunFixture(Func<Fixture, Task> test) => Sta.Run(async () =>
    {
        await using var fixture = await new Fixture().StartAsync();
        await test(fixture);
    });

    [Fact]
    public Task A_fresh_install_stays_offline_and_sends_no_requests() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        Assert.True(fixture.ViewModel.IsSignedOut);
        Assert.True(fixture.ViewModel.ShowLoginCard);
        Assert.False(fixture.ViewModel.CanSync);
        Assert.Equal(0, fixture.Sync.RefreshCalls);
        Assert.Equal(0, fixture.Auth.LoginCalls);
    });

    [Fact]
    public Task Choosing_offline_hides_the_login_card_until_the_user_connects_again() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        fixture.ViewModel.UseOfflineCommand.Execute(null);
        Assert.True(fixture.ViewModel.ShowOfflineNote);
        Assert.False(fixture.ViewModel.ShowLoginCard);

        fixture.ViewModel.ConnectCommand.Execute(null);
        Assert.False(fixture.ViewModel.ShowOfflineNote);
        Assert.True(fixture.ViewModel.ShowLoginCard);
    });

    [Fact]
    public Task The_account_choice_exposes_exactly_four_entries_and_no_desktop_forms() => Run(async () =>
    {
        // The desktop keeps no registration, email+password or reset form of its own: those
        // live in the browser. This pins the ViewModel surface so one cannot creep back in.
        var forbidden = new[] { "LoginCommand", "RegisterCommand", "SendCodeCommand", "ForgotCommand", "ResetCommand",
            "ShowRegisterCommand", "BackToLoginCommand", "Email", "Password", "ConfirmPassword", "NewPassword",
            "ConfirmNewPassword", "Code", "IsRegisterMode", "IsForgotMode", "IsResetMode", "IsFormMode" };
        foreach (var name in forbidden)
            Assert.Null(typeof(AccountViewModel).GetProperty(name));

        Assert.Equal(new[] { "Login", "Device" }, Enum.GetNames<AccountMode>());
        await Task.CompletedTask;
    });

    [Fact]
    public Task Forgot_password_opens_the_configured_servers_device_page_in_reset_mode() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        fixture.ViewModel.ShowForgotCommand.Execute(null);

        var opened = Assert.Single(fixture.Opened);
        Assert.Equal("https://sync.example.com/device?mode=reset", opened.ToString());
        Assert.True(fixture.ViewModel.IsLoginMode); // no desktop reset form: the page stays on the four entries
        Assert.False(fixture.ViewModel.IsError);
    });

    [Fact]
    public Task Forgot_password_falls_back_to_the_shipped_instance_without_a_server() => Run(async () =>
    {
        await using var fixture = await new Fixture().StartAsync();
        fixture.Auth.State = AuthState.SignedOut();
        var viewModel = new AccountViewModel(fixture.Auth, fixture.Sync, fixture.Settings, fixture.Dialogs,
            new Dispatcher(), fixture.Diagnostics, fixture.Opened.Add, fixture.Copied.Add);
        await using var scope = viewModel;
        await viewModel.InitializeAsync();

        viewModel.ShowForgotCommand.Execute(null);

        var opened = Assert.Single(fixture.Opened);
        Assert.Equal("https://mosaic.bansonic.top/device?mode=reset", opened.ToString());
    });

    [Fact]
    public Task Saving_a_self_hosted_server_returns_to_the_four_entries() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.ToggleServerPanelCommand.Execute(null);
        Assert.True(fixture.ViewModel.ShowServerPanel);
        fixture.ViewModel.ServerInput = "https://self.example.com";

        await fixture.ViewModel.SaveServerCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.ShowServerPanel);
        Assert.True(fixture.ViewModel.IsLoginMode);
        Assert.True(fixture.ViewModel.ShowLoginCard); // saving an address is not an account choice
        Assert.False(fixture.ViewModel.IsError);
    });


    [Fact]
    public Task Signing_out_asks_for_confirmation_before_clearing_credentials() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        signedIn.Dialogs.ConfirmResult = false;
        await signedIn.ViewModel.InitializeAsync();

        await signedIn.ViewModel.LogoutCommand.ExecuteAsync(null);
        Assert.Equal(0, signedIn.Auth.LogoutCalls);

        signedIn.Dialogs.ConfirmResult = true;
        await signedIn.ViewModel.LogoutCommand.ExecuteAsync(null);
        Assert.Equal(1, signedIn.Auth.LogoutCalls);
    });

    [Fact]
    public Task Device_sign_in_shows_the_hyphenated_code_and_opens_the_browser() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.StartDeviceCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.IsDeviceMode);
        Assert.Equal("BAAD-C832", fixture.ViewModel.DeviceCode);
        Assert.Equal("https://sync.example.com/device?code=BAADC832", fixture.ViewModel.DeviceUri);
        Assert.Equal("https://sync.example.com/device?code=BAADC832", Assert.Single(fixture.Opened).ToString());
        Assert.Null(fixture.Auth.LastServerInput);
        Assert.True(fixture.ViewModel.IsDeviceRunning);
        Assert.True(fixture.ViewModel.OpenDeviceUriCommand.CanExecute(null));
    });

    [Fact]
    public Task Cancelling_device_sign_in_clears_the_code_and_tells_the_service() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.StartDeviceCommand.ExecuteAsync(null);

        await fixture.ViewModel.CancelDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Auth.CancelDeviceCalls);
        Assert.Equal("", fixture.ViewModel.DeviceCode);
        Assert.False(fixture.ViewModel.IsDeviceMode);
    });

    [Fact]
    public Task Server_box_suggests_the_shipped_instance_when_none_is_configured() => Run(async () =>
    {
        await using var fixture = await new Fixture().StartAsync();
        fixture.Auth.State = AuthState.SignedOut();
        var viewModel = new AccountViewModel(fixture.Auth, fixture.Sync, fixture.Settings, fixture.Dialogs,
            new Dispatcher(), fixture.Diagnostics, fixture.Opened.Add, fixture.Copied.Add);
        await using var scope = viewModel;

        Assert.False(viewModel.IsServerConfigured);
        Assert.Equal("", viewModel.ServerText);
        Assert.Equal(AuthValidation.DefaultServerUrl, viewModel.ServerInput);
        Assert.Equal("https://mosaic.bansonic.top", AuthValidation.DefaultServerUrl);

        await viewModel.SaveServerCommand.ExecuteAsync(null);

        Assert.Equal("https://mosaic.bansonic.top", fixture.Auth.LastServerInput);
    });

    [Fact]
    public Task Server_box_keeps_a_configured_server_instead_of_the_default() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        Assert.True(fixture.ViewModel.IsServerConfigured);
        Assert.Equal("https://sync.example.com", fixture.ViewModel.ServerInput);
        Assert.NotEqual(AuthValidation.DefaultServerUrl, fixture.ViewModel.ServerInput);
    });

    [Fact]
    public Task Saving_a_server_address_normalizes_the_entered_value() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.ServerInput = "https://sync.example.com";

        await fixture.ViewModel.SaveServerCommand.ExecuteAsync(null);

        Assert.Equal("https://sync.example.com", fixture.Auth.LastServerInput);
        Assert.False(fixture.ViewModel.ShowServerPanel);
    });

    [Fact]
    public Task Downloading_a_backup_never_touches_local_tasks_without_confirmation() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        await signedIn.ViewModel.InitializeAsync();
        signedIn.Dialogs.ConfirmResult = false;

        await signedIn.ViewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, signedIn.Dialogs.Confirms);
        Assert.Equal(0, signedIn.Sync.DownloadCalls);

        signedIn.Dialogs.ConfirmResult = true;
        await signedIn.ViewModel.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(1, signedIn.Sync.DownloadCalls);
        Assert.True(signedIn.ViewModel.HasRestore);
    });

    [Fact]
    public Task Uploading_passes_the_include_settings_toggle() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        await signedIn.ViewModel.InitializeAsync();
        signedIn.ViewModel.IncludeOnUpload = false;

        await signedIn.ViewModel.UploadCommand.ExecuteAsync(null);

        Assert.Equal(1, signedIn.Sync.UploadCalls);
        Assert.False(signedIn.Sync.LastIncludeSettings);
        Assert.False(signedIn.ViewModel.IsError);
    });

    [Fact]
    public Task Upload_and_download_are_disabled_while_signed_out() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        Assert.False(fixture.ViewModel.CanSync);
        Assert.False(fixture.ViewModel.UploadCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.DownloadCommand.CanExecute(null));
    });

    [Fact]
    public Task Toggling_sync_options_persists_them_through_the_settings_session() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        fixture.ViewModel.SyncEnabled = false;
        fixture.ViewModel.IncludeSettings = false;

        for (var attempt = 0; attempt < 50 && fixture.Settings.Commits.Count < 2; attempt++) await Task.Delay(20);
        Assert.Contains(fixture.Settings.Commits, commit => commit.TryGetValue("sync.enabled", out var value) && value is false);
        Assert.Contains(fixture.Settings.Commits, commit => commit.TryGetValue("sync.includeSettings", out var value) && value is false);
    });

    [Fact]
    public Task Sessions_are_loaded_when_the_page_opens_signed_in() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        signedIn.Auth.SessionsResult =
        [
            new("s-1", "Mosaic 桌面客户端", "windows", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(10), true),
            new("s-2", "Mosaic 手机", "android", DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(5), false)
        ];

        await signedIn.ViewModel.InitializeAsync();

        Assert.Equal(1, signedIn.Auth.ListSessionsCalls);
        Assert.Equal(2, signedIn.ViewModel.Sessions.Count);
        Assert.True(signedIn.ViewModel.HasSessions);
        Assert.True(signedIn.ViewModel.ShowSessionList);
    });

    [Fact]
    public Task Revoking_a_session_removes_it_from_the_list() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        signedIn.Auth.SessionsResult =
        [
            new("s-1", "Mosaic 桌面客户端", "windows", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(10), true),
            new("s-2", "Mosaic 手机", "android", DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(5), false)
        ];
        await signedIn.ViewModel.InitializeAsync();

        await signedIn.ViewModel.RevokeSessionCommand.ExecuteAsync("s-2");

        var remaining = Assert.Single(signedIn.ViewModel.Sessions);
        Assert.Equal("s-1", remaining.Id);
        Assert.False(signedIn.ViewModel.IsError);
    });

    [Fact]
    public Task Deleting_the_account_asks_for_confirmation_and_never_deletes_silently() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        await signedIn.ViewModel.InitializeAsync();

        signedIn.ViewModel.ShowDeleteDialogCommand.Execute(null);
        Assert.True(signedIn.ViewModel.DeleteDialogVisible);
        Assert.True(signedIn.ViewModel.DeleteUsePassword);

        signedIn.Dialogs.ConfirmResult = false;
        signedIn.ViewModel.DeleteCredential = "correct horse battery";
        await signedIn.ViewModel.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, signedIn.Auth.DeleteAccountCalls);

        signedIn.Dialogs.ConfirmResult = true;
        await signedIn.ViewModel.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, signedIn.Auth.DeleteAccountCalls);
        Assert.Equal("correct horse battery", signedIn.Auth.LastDeletePassword);
        Assert.Null(signedIn.Auth.LastDeleteCode);
        Assert.False(signedIn.ViewModel.DeleteDialogVisible);
        Assert.False(signedIn.ViewModel.IsError);
    });

    [Fact]
    public Task Deleting_with_an_email_code_validates_the_code_format() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        await signedIn.ViewModel.InitializeAsync();

        signedIn.ViewModel.ShowDeleteDialogCommand.Execute(null);
        signedIn.ViewModel.DeleteUseCode = true;
        signedIn.ViewModel.DeleteCredential = "12ab";

        await signedIn.ViewModel.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(0, signedIn.Auth.DeleteAccountCalls);
        Assert.True(signedIn.ViewModel.IsError);

        signedIn.ViewModel.DeleteCredential = "123456";
        await signedIn.ViewModel.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, signedIn.Auth.DeleteAccountCalls);
        Assert.Equal("123456", signedIn.Auth.LastDeleteCode);
        Assert.Null(signedIn.Auth.LastDeletePassword);
    });

    [Fact]
    public Task Sending_the_delete_code_uses_the_delete_account_purpose() => RunFixture(async fixture =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        await signedIn.ViewModel.InitializeAsync();

        signedIn.ViewModel.ShowDeleteDialogCommand.Execute(null);
        signedIn.ViewModel.DeleteUseCode = true;
        await signedIn.ViewModel.SendDeleteCodeCommand.ExecuteAsync(null);

        Assert.Equal(1, signedIn.Auth.SendCodeCalls);
        Assert.Equal("user@example.com", signedIn.Auth.LastEmail);
        Assert.False(signedIn.ViewModel.IsError);
    });

    [Fact]
    public Task A_fresh_install_stays_offline_until_the_user_chooses() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        Assert.True(fixture.ViewModel.IsSignedOut);
        Assert.True(fixture.ViewModel.IsLoginMode);
        Assert.True(fixture.ViewModel.ShowLoginCard);
        Assert.Equal(0, fixture.Auth.LoginCalls);
        Assert.Equal(0, fixture.Sync.RefreshCalls);
    });

    [Fact]
    public Task Choosing_offline_shows_the_note_without_writing_state() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        fixture.ViewModel.UseOfflineCommand.Execute(null);

        Assert.True(fixture.ViewModel.ShowOfflineNote);
        Assert.False(fixture.ViewModel.ShowLoginCard);
        Assert.Empty(fixture.Settings.Commits);
    });

    [Fact]
    public Task The_factory_restores_the_offline_choice_on_the_next_visit() => Run(async () =>
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var saved = false;
        await using var model = new AccountViewModel(
            fixture.Auth, fixture.Sync, fixture.Settings, fixture.Dialogs, new Dispatcher(), fixture.Diagnostics,
            fixture.Opened.Add, fixture.Copied.Add, offlineChosenChanged: value => saved = value);

        await model.InitializeAsync();
        model.UseOfflineCommand.Execute(null);
        Assert.True(saved);

        await using var restored = new AccountViewModel(
            fixture.Auth, fixture.Sync, fixture.Settings, fixture.Dialogs, new Dispatcher(), fixture.Diagnostics,
            fixture.Opened.Add, fixture.Copied.Add, offlineChosen: saved);
        await restored.InitializeAsync();
        Assert.True(restored.ShowOfflineNote);
        Assert.False(restored.ShowLoginCard);
    });

    [Fact]
    public Task A_signed_in_install_never_shows_the_login_card() => RunFixture(async _ =>
    {
        await using var signedIn = await new Fixture().StartAsync(signedIn: true);
        await signedIn.ViewModel.InitializeAsync();

        Assert.True(signedIn.ViewModel.IsSignedIn);
        Assert.False(signedIn.ViewModel.ShowLoginCard);
        Assert.True(signedIn.ViewModel.ShowAccountSettings);
    });

    [Fact]
    public Task Configuring_a_server_alone_writes_no_first_run_choice() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.SaveServerCommand.ExecuteAsync(null);
        Assert.True(fixture.ViewModel.IsServerConfigured);
        Assert.True(fixture.ViewModel.ShowLoginCard);
        Assert.DoesNotContain(fixture.Settings.Commits, draft => draft.ContainsKey("auth.accountChoiceMade"));
    });

    [Fact]
    public Task Offline_exit_opens_start_without_calling_logout() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.LogoutCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.Auth.LogoutCalls);
        Assert.Equal(0, fixture.Dialogs.Confirms);
        Assert.True(fixture.ViewModel.ShowStartPage);
    });

    [Fact]
    public Task Signing_out_clears_sessions_restore_summary_and_snapshot() => Run(async () =>
    {
        await using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await fixture.StartAsync(signedIn: true);
        // StartAsync 会重置服务替身的初始状态，备份/恢复摘要必须在启动之后再布置。
        fixture.Auth.SessionsResult =
        [
            new AuthSessionInfoDto("session-1", "Windows", "Windows 11", now.AddDays(-1), now, now.AddDays(30), true)
        ];
        fixture.Sync.State = new(true, false,
            new BackupSnapshotDto("snap-1", now, 3, "sha256", 100), new RestoreSummary(3, 2), null);
        await fixture.ViewModel.InitializeAsync();
        Assert.True(fixture.ViewModel.HasRestore);

        await fixture.ViewModel.LogoutCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Auth.LogoutCalls);
        Assert.True(fixture.ViewModel.IsSignedOut);
        Assert.True(fixture.ViewModel.ShowStartPage);
        Assert.Empty(fixture.ViewModel.Sessions);
        // SnapshotSummary 在无备份时回退为本地化提示文本，摘要是否清空由 RestoreSummary 判定。
        Assert.Null(fixture.ViewModel.RestoreSummary);
        Assert.False(fixture.ViewModel.HasRestore);
    });

    [Fact]
    public Task A_signed_out_account_page_shows_the_login_card() => Run(async () =>
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ViewModel.InitializeAsync();

        // 启动页固定为任务页后，用户从设置进入账户页也必须看到登录入口，而不是空白设置页。
        Assert.True(fixture.ViewModel.ShowStartPage);
        Assert.False(fixture.ViewModel.ShowAccountSettings);
        Assert.True(fixture.ViewModel.ShowLoginCard);
    });

    [Fact]
    public Task A_signed_in_account_page_switches_from_the_start_page_to_settings() => Run(async () =>
    {
        await using var fixture = await new Fixture().StartAsync(signedIn: true);
        await fixture.ViewModel.InitializeAsync();

        Assert.True(fixture.ViewModel.ShowAccountSettings);
        Assert.False(fixture.ViewModel.ShowStartPage);
    });

    [Fact]
    public Task Device_sign_in_without_a_server_is_refused_instead_of_using_the_default() => Run(async () =>
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Auth.State = AuthState.SignedOut();
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.StartDeviceCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.Auth.StartDeviceCalls);
        Assert.True(fixture.ViewModel.ShowServerPanel);
        Assert.True(fixture.ViewModel.IsError);
    });

    [Fact]
    public Task Browser_approval_of_the_device_code_signs_in_and_stops_polling() => Run(async () =>
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Auth.PollStatus = DeviceAuthorizationStatus.Approved;
        fixture.Auth.DeviceAuthorization = new("device-code", "BAADC832", new Uri("https://sync.example.com/device"),
            new Uri("https://sync.example.com/device?code=BAADC832"), 60, 2);
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.StartDeviceCommand.ExecuteAsync(null);
        for (var attempt = 0; attempt < 200 && fixture.ViewModel.IsDeviceRunning; attempt++) await Task.Delay(25);

        Assert.Equal(1, fixture.Auth.StartDeviceCalls);
        Assert.False(fixture.ViewModel.IsDeviceRunning);
        Assert.True(fixture.ViewModel.IsSignedIn);
    });
}
