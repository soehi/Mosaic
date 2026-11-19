using Mosaic.Auth.Contracts;
using Mosaic.Auth.Wpf.Internal;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;

namespace Mosaic.Wpf.Tests;

// The account page is the only place that knows about authentication. These tests pin the
// behaviour the shell relies on: offline by default, no network without a server, local form
// validation, the browser code format, and confirmation before a destructive restore.
public sealed class AccountViewModelTests
{
    private static readonly Uri Server = new("https://sync.example.com/");
    private static readonly AuthAccountDto Account = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户");

    private sealed class FakeAuth : IAuthApi
    {
        public AuthState State { get; set; } = AuthState.SignedOut();
        public Guid DeviceId { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public event EventHandler? StateChanged { add { } remove { } }
        public int LoginCalls, RegisterCalls, SendCodeCalls, StartDeviceCalls, CancelDeviceCalls, LogoutCalls;
        public string? LastEmail, LastCode, LastPassword, LastServerInput;
        public OperationResult LoginResult = OperationResult.Success();
        public OperationResult RegisterResult = OperationResult.Success();
        public OperationResult SendCodeResult = OperationResult.Success();
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
        public Task<OperationResult<DeviceAuthorizationStatus>> PollDeviceAuthorizationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<DeviceAuthorizationStatus>.Success(DeviceAuthorizationStatus.Pending));
        public Task<OperationResult> CancelDeviceAuthorizationAsync() { CancelDeviceCalls++; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> LogoutAsync(CancellationToken cancellationToken = default) { LogoutCalls++; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<string>.Success("access"));
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
            ViewModel = new AccountViewModel(Auth, Sync, Settings, Dialogs, new Dispatcher(), Diagnostics, Opened.Add, Copied.Add);
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
    public Task Invalid_email_is_rejected_before_any_network_call() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.Email = "not-an-email";
        fixture.ViewModel.Password = "correct horse battery";

        await fixture.ViewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.IsError);
        Assert.Equal(0, fixture.Auth.LoginCalls);
    });

    [Fact]
    public Task Short_passwords_are_rejected_locally() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.Email = "user@example.com";
        fixture.ViewModel.Password = "short";

        await fixture.ViewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.IsError);
        Assert.Equal(0, fixture.Auth.LoginCalls);
    });

    [Fact]
    public Task Registration_requires_the_code_and_a_matching_confirmation() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.ShowRegisterCommand.Execute(null);
        fixture.ViewModel.Email = "user@example.com";
        fixture.ViewModel.Password = "correct horse battery";
        fixture.ViewModel.ConfirmPassword = "different password";

        await fixture.ViewModel.RegisterCommand.ExecuteAsync(null);
        Assert.True(fixture.ViewModel.IsError);
        Assert.Equal(0, fixture.Auth.RegisterCalls);

        fixture.ViewModel.Code = "12ab";
        await fixture.ViewModel.RegisterCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Auth.RegisterCalls);
    });

    [Fact]
    public Task Registration_passes_the_trimmed_email_code_and_password_to_the_service() => RunFixture(async fixture =>
    {
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.ShowRegisterCommand.Execute(null);
        fixture.ViewModel.Email = " User@Example.com ";
        fixture.ViewModel.Code = "123456";
        fixture.ViewModel.Password = "correct horse battery";
        fixture.ViewModel.ConfirmPassword = "correct horse battery";

        await fixture.ViewModel.RegisterCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Auth.RegisterCalls);
        Assert.Equal("User@Example.com", fixture.Auth.LastEmail);
        Assert.Equal("123456", fixture.Auth.LastCode);
        Assert.Equal("correct horse battery", fixture.Auth.LastPassword);
        Assert.False(fixture.ViewModel.IsError);
        Assert.True(fixture.ViewModel.IsLoginMode);
    });

    [Fact]
    public Task A_failed_login_shows_the_localized_error_and_keeps_the_typed_email() => RunFixture(async fixture =>
    {
        fixture.Auth.LoginResult = OperationResult.Failure(new(AuthErrorCodes.InvalidCredentials, "bad"));
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.Email = "user@example.com";
        fixture.ViewModel.Password = "correct horse battery";

        await fixture.ViewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.IsError);
        Assert.True(fixture.ViewModel.HasMessage);
        Assert.Equal("user@example.com", fixture.ViewModel.Email);
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
        Assert.Single(fixture.Opened);
        Assert.True(fixture.ViewModel.IsDeviceRunning);
        Assert.True(fixture.ViewModel.OpenDeviceUriCommand.CanExecute(null));
    });

    [Fact]
    public Task Starting_device_sign_in_without_a_server_asks_for_one_first() => RunFixture(async fixture =>
    {
        fixture.Auth.State = AuthState.SignedOut();
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.StartDeviceCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.IsError);
        Assert.True(fixture.ViewModel.ShowServerPanel);
        Assert.Equal(0, fixture.Auth.StartDeviceCalls);
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
}
