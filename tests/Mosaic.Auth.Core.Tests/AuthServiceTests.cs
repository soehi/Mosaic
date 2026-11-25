using Mosaic.Auth.Contracts;
using Mosaic.Auth.Core;
using Mosaic.Contracts;

namespace Mosaic.Auth.Core.Tests;

public sealed class AuthServiceTests
{
    private const string ServerUrl = "https://sync.example.com/";
    private static readonly Uri Server = new(ServerUrl);

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public MemorySecretStore Secrets { get; } = new();
        public FakeSettings Settings { get; } = new();
        public FakeTransport Transport { get; } = new();
        public Diagnostics Diagnostics { get; } = new();
        public AuthModule? Module { get; private set; }
        public IAuthApi Api => Module!.Api;

        public async Task<Fixture> StartAsync()
        {
            Module = new AuthModule(new(Transport, Secrets, Settings, Clock, new Gate(), Diagnostics));
            await Module.ActivateAsync();
            return this;
        }

        public async Task SignInAsync()
        {
            Assert.True((await Api.SetServerAsync(ServerUrl)).IsSuccess);
            var login = await Api.LoginAsync("user@example.com", "password-123");
            Assert.True(login.IsSuccess, login.Error?.Message);
        }

        /// <summary>预置“上次运行留下的凭据”：服务器地址 + 平台密钥库里的 refresh token，模拟应用重启。</summary>
        public Fixture WithStoredCredential()
        {
            Settings.Values["auth.serverUrl"] = ServerUrl;
            Secrets.Stored = new("refresh-1", Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", "用户", Server);
            return this;
        }
    }

    [Fact]
    public async Task Activation_without_server_stays_signed_out_and_never_contacts_network()
    {
        var fixture = await new Fixture().StartAsync();

        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Null(fixture.Api.State.Server);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Contains("auth-core", fixture.Settings.RegisteredModules);
    }

    [Fact]
    public async Task Activation_with_stored_credentials_for_same_server_signs_in_offline()
    {
        var fixture = new Fixture().WithStoredCredential();
        await fixture.StartAsync();

        Assert.Equal(AuthStatus.SignedIn, fixture.Api.State.Status);
        Assert.Equal("user@example.com", fixture.Api.State.Account!.Email);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), fixture.Api.DeviceId);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Fact]
    public async Task Activation_with_credentials_bound_to_another_server_signs_out_without_sending_them()
    {
        var fixture = new Fixture();
        fixture.Settings.Values["auth.serverUrl"] = ServerUrl;
        fixture.Secrets.Stored = new("refresh-1", Guid.Empty,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "user@example.com", null, new Uri("https://other.example.com/"));
        await fixture.StartAsync();

        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Equal(Server, fixture.Api.State.Server);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.NotEqual(Guid.Empty, fixture.Api.DeviceId);
    }

    [Theory]
    [InlineData("http://sync.example.com")]
    [InlineData("https://user:secret@sync.example.com")]
    [InlineData("ftp://sync.example.com")]
    public async Task Set_server_rejects_untrusted_urls_and_keeps_settings_untouched(string value)
    {
        var fixture = await new Fixture().StartAsync();

        var result = await fixture.Api.SetServerAsync(value);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.ServerUntrusted, result.Error!.Code);
        Assert.Empty(fixture.Settings.Commits);
        Assert.Null(fixture.Api.State.Server);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Fact]
    public async Task Set_server_persists_only_the_normalized_origin()
    {
        var fixture = await new Fixture().StartAsync();

        var result = await fixture.Api.SetServerAsync("Sync.Example.COM:8443");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var commit = Assert.Single(fixture.Settings.Commits);
        Assert.Equal("https://sync.example.com:8443", commit["auth.serverUrl"]);
        Assert.Single(commit);
        Assert.Equal("https://sync.example.com:8443", fixture.Api.State.Server!.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public async Task Pasting_a_full_url_keeps_only_the_origin()
    {
        var fixture = await new Fixture().StartAsync();

        var result = await fixture.Api.SetServerAsync("https://sync.example.com/some/path?token=secret");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("https://sync.example.com", fixture.Api.State.Server!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("https://sync.example.com", Assert.Single(fixture.Settings.Commits)["auth.serverUrl"]);
    }

    [Fact]
    public async Task Server_address_is_hidden_from_the_generic_editor_but_still_saved_by_auth()
    {
        var fixture = await new Fixture().StartAsync();
        fixture.Settings.Values["auth.serverUrl"] = ServerUrl;

        var definition = Assert.Single(fixture.Settings.Definitions, item => item.Id == "auth.serverUrl");
        Assert.False(definition.ShowInSettings);

        var result = await fixture.Api.SetServerAsync("https://other.example.com");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("https://other.example.com", Assert.Single(fixture.Settings.Commits)["auth.serverUrl"]);
    }

    [Fact]
    public async Task Changing_server_drops_credentials_issued_by_the_previous_origin()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.SignInAsync();
        Assert.Equal(AuthStatus.SignedIn, fixture.Api.State.Status);

        var changed = await fixture.Api.SetServerAsync("https://other.example.com");

        Assert.True(changed.IsSuccess);
        Assert.Equal(1, fixture.Secrets.Clears);
        Assert.Null(fixture.Secrets.Stored);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Equal("https://other.example.com", fixture.Api.State.Server!.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public async Task Register_validates_email_code_and_password_before_any_network_call()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);

        var badEmail = await fixture.Api.RegisterAsync("not-an-email", "123456", "password-123");
        var badCode = await fixture.Api.RegisterAsync("user@example.com", "12ab56", "password-123");
        var badPassword = await fixture.Api.RegisterAsync("user@example.com", "123456", "short");

        Assert.Equal("Email", badEmail.Error!.Field);
        Assert.Equal("Code", badCode.Error!.Field);
        Assert.Equal("Password", badPassword.Error!.Field);
        Assert.All(new[] { badEmail, badCode, badPassword }, result => Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code));
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
    }

    [Fact]
    public async Task Register_success_persists_refresh_token_and_never_writes_secrets_to_settings()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);

        var result = await fixture.Api.RegisterAsync("User@Example.com", "123456", "password-123");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(AuthStatus.SignedIn, fixture.Api.State.Status);
        var stored = Assert.IsType<AuthSecrets>(fixture.Secrets.Stored);
        Assert.Equal("refresh-1", stored.RefreshToken);
        Assert.Equal("user@example.com", stored.Email);
        Assert.Equal(Server, stored.Server);
        Assert.Equal(fixture.Api.DeviceId, stored.DeviceId);
        // The only settings write is the server address; the access and refresh tokens stay out.
        Assert.All(fixture.Settings.Commits, commit =>
        {
            Assert.Equal(new[] { "auth.serverUrl" }, commit.Keys.ToArray());
            Assert.DoesNotContain("refresh-1", commit.Values.Select(value => value?.ToString()));
            Assert.DoesNotContain("access-1", commit.Values.Select(value => value?.ToString()));
        });
    }

    [Fact]
    public async Task Register_fails_when_credentials_cannot_be_saved_locally()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        fixture.Secrets.FailSave = true;

        var result = await fixture.Api.RegisterAsync("user@example.com", "123456", "password-123");

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.SecretsUnavailable, result.Error!.Code);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Null(fixture.Secrets.Stored);
    }

    [Fact]
    public async Task Login_maps_invalid_credentials_to_a_localized_message_key()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        fixture.Transport.Session = OperationResult<AuthSessionDto>.Failure(new(AuthErrorCodes.InvalidCredentials, "invalid"));

        var result = await fixture.Api.LoginAsync("user@example.com", "password-123");

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.InvalidCredentials, result.Error!.Code);
        Assert.Equal("Auth.Error." + AuthErrorCodes.InvalidCredentials, result.Error.MessageKey);
        Assert.Equal("邮箱或密码不正确。", result.Error.Message);
    }

    [Fact]
    public async Task Login_rejects_a_session_without_credentials_and_stays_signed_out()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        fixture.Transport.Session = OperationResult<AuthSessionDto>.Success(FakeTransport.DefaultSession(accessToken: ""));

        var result = await fixture.Api.LoginAsync("user@example.com", "password-123");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Null(fixture.Secrets.Stored);
    }

    [Fact]
    public async Task Logout_revokes_the_remote_session_then_clears_the_local_secret()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.SignInAsync();
        var changes = 0;
        fixture.Api.StateChanged += (_, _) => changes++;

        var result = await fixture.Api.LogoutAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Transport.LogoutCalls);
        Assert.Equal((Server, "access-1", "refresh-1"), fixture.Transport.LastLogout);
        Assert.Equal(1, fixture.Secrets.Clears);
        Assert.Null(fixture.Secrets.Stored);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task Logout_still_clears_locally_when_revocation_fails()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.SignInAsync();
        fixture.Transport.LogoutResult = OperationResult.Failure(new(AuthErrorCodes.NetworkUnavailable, "offline"));

        var result = await fixture.Api.LogoutAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Secrets.Clears);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
    }

    [Fact]
    public async Task Polling_before_starting_device_authorization_reports_flow_not_started()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);

        var poll = await fixture.Api.PollDeviceAuthorizationAsync();

        Assert.False(poll.IsSuccess);
        Assert.Equal(AuthErrorCodes.DeviceFlowNotStarted, poll.Error!.Code);
        Assert.Equal(0, fixture.Transport.PollCalls);
    }

    [Fact]
    public async Task Device_authorization_requires_a_server_first()
    {
        var fixture = await new Fixture().StartAsync();

        var started = await fixture.Api.StartDeviceAuthorizationAsync();

        Assert.False(started.IsSuccess);
        Assert.Equal(AuthErrorCodes.ServerRequired, started.Error!.Code);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Fact]
    public async Task Device_flow_reports_pending_then_expires_locally_after_the_deadline()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        fixture.Transport.DeviceStart = OperationResult<DeviceAuthorizationDto>.Success(
            new("device-code", "BAADC832", new Uri("https://sync.example.com/device"), null, 120, 5));

        Assert.True((await fixture.Api.StartDeviceAuthorizationAsync()).IsSuccess);
        var pending = await fixture.Api.PollDeviceAuthorizationAsync();
        Assert.Equal(DeviceAuthorizationStatus.Pending, pending.Value.Status);
        var callsWhilePending = fixture.Transport.PollCalls;

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(10);
        var expired = await fixture.Api.PollDeviceAuthorizationAsync();

        Assert.Equal(DeviceAuthorizationStatus.Expired, expired.Value.Status);
        Assert.Equal(callsWhilePending, fixture.Transport.PollCalls);
        Assert.Equal(AuthErrorCodes.DeviceFlowNotStarted, (await fixture.Api.PollDeviceAuthorizationAsync()).Error!.Code);
    }

    [Fact]
    public async Task Device_flow_approval_signs_in_and_stops_polling()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        Assert.True((await fixture.Api.StartDeviceAuthorizationAsync()).IsSuccess);
        fixture.Transport.DevicePoll = OperationResult<DevicePollDto>.Success(
            new(DeviceAuthorizationStatus.Approved, FakeTransport.DefaultSession()));

        var approved = await fixture.Api.PollDeviceAuthorizationAsync();

        Assert.Equal(DeviceAuthorizationStatus.Approved, approved.Value.Status);
        Assert.Equal(AuthStatus.SignedIn, fixture.Api.State.Status);
        Assert.NotNull(fixture.Secrets.Stored);
        Assert.Equal(AuthErrorCodes.DeviceFlowNotStarted, (await fixture.Api.PollDeviceAuthorizationAsync()).Error!.Code);
    }

    [Fact]
    public async Task Device_flow_denial_reports_denied_and_stops_polling()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        Assert.True((await fixture.Api.StartDeviceAuthorizationAsync()).IsSuccess);
        fixture.Transport.DevicePoll = OperationResult<DevicePollDto>.Success(new(DeviceAuthorizationStatus.Denied, null));

        var denied = await fixture.Api.PollDeviceAuthorizationAsync();

        Assert.Equal(DeviceAuthorizationStatus.Denied, denied.Value.Status);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Equal(AuthErrorCodes.DeviceFlowNotStarted, (await fixture.Api.PollDeviceAuthorizationAsync()).Error!.Code);
    }

    [Fact]
    public async Task Canceling_device_authorization_prevents_further_polling()
    {
        var fixture = await new Fixture().StartAsync();
        Assert.True((await fixture.Api.SetServerAsync(ServerUrl)).IsSuccess);
        Assert.True((await fixture.Api.StartDeviceAuthorizationAsync()).IsSuccess);

        Assert.True((await fixture.Api.CancelDeviceAuthorizationAsync()).IsSuccess);

        Assert.Equal(AuthErrorCodes.DeviceFlowNotStarted, (await fixture.Api.PollDeviceAuthorizationAsync()).Error!.Code);
        Assert.Equal(0, fixture.Transport.PollCalls);
    }

    [Fact]
    public async Task Restored_session_redeems_the_stored_refresh_token_on_the_first_token_request()
    {
        var fixture = new Fixture().WithStoredCredential();
        await fixture.StartAsync();
        Assert.Equal(0, fixture.Transport.Calls);

        var token = await fixture.Api.GetAccessTokenAsync();

        Assert.True(token.IsSuccess, token.Error?.Message);
        Assert.Equal("access-1", token.Value);
        Assert.Equal(1, fixture.Transport.RefreshCalls);
        Assert.Equal("refresh-1", fixture.Secrets.Stored!.RefreshToken);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), fixture.Secrets.Stored.DeviceId);
        Assert.Equal(AuthStatus.SignedIn, fixture.Api.State.Status);
        Assert.Equal("user@example.com", fixture.Api.State.Account!.Email);
    }

    [Fact]
    public async Task Restored_session_with_a_rejected_refresh_token_clears_the_credential_and_signs_out()
    {
        var fixture = new Fixture().WithStoredCredential();
        await fixture.StartAsync();
        fixture.Transport.RefreshResult = OperationResult<AuthSessionDto>.Failure(new(AuthErrorCodes.TokenInvalid, "invalid"));

        var token = await fixture.Api.GetAccessTokenAsync();

        Assert.False(token.IsSuccess);
        Assert.Equal(AuthErrorCodes.TokenInvalid, token.Error!.Code);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Null(fixture.Secrets.Stored);
        Assert.Equal(1, fixture.Secrets.Clears);
    }

    [Fact]
    public async Task Restored_session_keeps_the_credential_when_the_refresh_cannot_reach_the_server()
    {
        var fixture = new Fixture().WithStoredCredential();
        await fixture.StartAsync();
        fixture.Transport.RefreshResult = OperationResult<AuthSessionDto>.Failure(new(AuthErrorCodes.NetworkUnavailable, "offline"));

        var token = await fixture.Api.GetAccessTokenAsync();

        Assert.False(token.IsSuccess);
        Assert.Equal(AuthStatus.SignedIn, fixture.Api.State.Status);
        Assert.NotNull(fixture.Secrets.Stored);
        Assert.Equal(0, fixture.Secrets.Clears);
    }

    [Fact]
    public async Task Access_token_is_cached_until_it_nearly_expires_then_refreshed_once()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.SignInAsync();

        var first = await fixture.Api.GetAccessTokenAsync();
        var cached = await fixture.Api.GetAccessTokenAsync();

        Assert.Equal("access-1", first.Value);
        Assert.Equal("access-1", cached.Value);
        Assert.Equal(0, fixture.Transport.RefreshCalls);

        fixture.Transport.RefreshResult = OperationResult<AuthSessionDto>.Success(
            FakeTransport.DefaultSession(accessToken: "access-2", refreshToken: "refresh-2"));
        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
        var refreshed = await fixture.Api.GetAccessTokenAsync();

        Assert.Equal("access-2", refreshed.Value);
        Assert.Equal(1, fixture.Transport.RefreshCalls);
        Assert.Equal("refresh-2", fixture.Secrets.Stored!.RefreshToken);
    }

    [Fact]
    public async Task Invalid_refresh_token_clears_the_local_credential_and_signs_out()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.SignInAsync();
        fixture.Transport.RefreshResult = OperationResult<AuthSessionDto>.Failure(new(AuthErrorCodes.TokenInvalid, "invalid"));
        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);

        var token = await fixture.Api.GetAccessTokenAsync();

        Assert.False(token.IsSuccess);
        Assert.Equal(AuthErrorCodes.TokenInvalid, token.Error!.Code);
        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.Null(fixture.Secrets.Stored);
        Assert.Equal(1, fixture.Secrets.Clears);
    }

    [Fact]
    public async Task Stopping_the_module_drops_the_session_but_keeps_the_stored_credential()
    {
        var fixture = await new Fixture().StartAsync();
        await fixture.SignInAsync();

        await fixture.Module!.StopAsync();

        Assert.Equal(AuthStatus.SignedOut, fixture.Api.State.Status);
        Assert.NotNull(fixture.Secrets.Stored);
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await fixture.Api.GetAccessTokenAsync()).Error!.Code);
    }
}
