using System.Net;
using System.Text.Json;
using Mosaic.Auth.Contracts;
using Mosaic.Auth.Http;
using Mosaic.Contracts;

namespace Mosaic.Auth.Http.Tests;

public sealed class HttpAuthTransportTests
{
    private static HttpAuthTransport Transport(bool allowLoopback = true) =>
        new(new HttpClient(), clock: null, allowInsecureLoopback: allowLoopback);

    private static string SessionJson(DateTimeOffset expiresAt, string access = "access-1", string refresh = "refresh-1") =>
        JsonSerializer.Serialize(new
        {
            accessToken = access,
            refreshToken = refresh,
            accessTokenExpiresAt = expiresAt,
            user = new { id = "11111111-1111-1111-1111-111111111111", email = "user@example.com", displayName = "用户" }
        });

    [Fact]
    public async Task Plain_http_is_refused_before_any_request_is_sent()
    {
        await using var server = new LoopbackServer();
        var transport = new HttpAuthTransport(new HttpClient());

        var result = await transport.GetPolicyAsync(server.BaseUri);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.ServerUntrusted, result.Error!.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Https_is_required_even_when_loopback_is_allowed()
    {
        var result = await Transport().GetPolicyAsync(new Uri("https://user:secret@sync.example.com/"));
        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.ServerUntrusted, result.Error!.Code);

        var insecure = await Transport().GetPolicyAsync(new Uri("http://sync.example.com/"));
        Assert.False(insecure.IsSuccess);
        Assert.Equal(AuthErrorCodes.ServerUntrusted, insecure.Error!.Code);
    }

    [Fact]
    public async Task Policy_is_read_from_the_v1_endpoint()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, """{"registrationOpen":true,"requireEmailVerification":true,"serverName":"Mosaic 自建"}""");

        var result = await Transport().GetPolicyAsync(server.BaseUri);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.RegistrationOpen);
        Assert.True(result.Value.RequireEmailVerification);
        Assert.Equal("Mosaic 自建", result.Value.ServerName);
        Assert.Equal("/v1/auth/policy", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task Login_parses_the_session_and_derives_expiry_from_the_server_timestamp()
    {
        await using var server = new LoopbackServer();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        server.Handler = (_, _) => (200, SessionJson(expiresAt));

        var result = await Transport().LoginAsync(server.BaseUri, "user@example.com", "password-123");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("access-1", result.Value.AccessToken);
        Assert.Equal("refresh-1", result.Value.RefreshToken);
        Assert.InRange(result.Value.ExpiresInSeconds, 480, 660);
        Assert.Equal("user@example.com", result.Value.Account.Email);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Value.Account.UserId);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/auth/login", request.Path);
        Assert.Contains("password-123", request.Body);
        Assert.Contains("deviceLabel", request.Body);
    }

    [Fact]
    public async Task Session_without_tokens_is_reported_as_an_internal_error()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, """{"user":{"id":"11111111-1111-1111-1111-111111111111","email":"user@example.com"}}""");

        var result = await Transport().LoginAsync(server.BaseUri, "user@example.com", "password-123");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
    }

    [Fact]
    public async Task Invalid_credentials_are_mapped_from_the_server_error_body()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (401, """{"code":"auth.invalid_credentials","message":"邮箱或密码不正确。","traceId":"trace-1"}""");

        var result = await Transport().LoginAsync(server.BaseUri, "user@example.com", "bad-password");

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.InvalidCredentials, result.Error!.Code);
        Assert.Equal("trace-1", result.Error.CorrelationId);
    }

    [Fact]
    public async Task Rate_limiting_maps_even_without_a_json_body()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (429, "");

        var result = await Transport().SendEmailCodeAsync(server.BaseUri, "user@example.com", AuthEmailCodePurpose.Register);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.RateLimited, result.Error!.Code);
    }

    [Fact]
    public async Task Logout_sends_the_bearer_token_and_the_refresh_token()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (204, "");

        var result = await Transport().LogoutAsync(server.BaseUri, "access-1", "refresh-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var request = Assert.Single(server.Requests);
        Assert.Equal("/v1/auth/logout", request.Path);
        Assert.Equal("Bearer access-1", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("refresh-1", body.RootElement.GetProperty("refreshToken").GetString());
        Assert.False(body.RootElement.GetProperty("allDevices").GetBoolean());
    }

    [Fact]
    public async Task Account_lookup_requires_both_the_bearer_token_and_a_valid_user_object()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, """{"user":{"id":"11111111-1111-1111-1111-111111111111","email":"user@example.com"}}""");

        var result = await Transport().GetAccountAsync(server.BaseUri, "access-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("user@example.com", result.Value.Email);
        Assert.Equal("Bearer access-1", Assert.Single(server.Requests).Authorization);
    }

    [Fact]
    public async Task Device_start_returns_the_formatted_code_and_verification_uri()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, """{"deviceCode":"device-1","userCode":"BAADC832","verificationUri":"https://sync.example.com/device","expiresIn":900,"interval":5}""");

        var result = await Transport().StartDeviceAuthorizationAsync(server.BaseUri);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("device-1", result.Value.DeviceCode);
        Assert.Equal("BAADC832", result.Value.UserCode);
        Assert.Equal(new Uri("https://sync.example.com/device"), result.Value.VerificationUri);
        Assert.Equal(900, result.Value.ExpiresInSeconds);
        Assert.Equal(5, result.Value.IntervalSeconds);
    }

    [Fact]
    public async Task Device_poll_reports_approved_with_the_session()
    {
        await using var server = new LoopbackServer();
        var session = JsonDocument.Parse(SessionJson(DateTimeOffset.UtcNow.AddMinutes(15))).RootElement;
        server.Handler = (_, _) => (200, $$"""{"status":"approved","session":{{session.GetRawText()}}}""");

        var result = await Transport().PollDeviceAuthorizationAsync(server.BaseUri, "device-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(DeviceAuthorizationStatus.Approved, result.Value.Status);
        Assert.Equal("access-1", result.Value.Session!.AccessToken);
    }

    [Fact]
    public async Task Device_poll_reports_pending_without_a_session()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, """{"status":"pending"}""");

        var result = await Transport().PollDeviceAuthorizationAsync(server.BaseUri, "device-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(DeviceAuthorizationStatus.Pending, result.Value.Status);
        Assert.Null(result.Value.Session);
    }

    [Fact]
    public async Task Unreachable_server_reports_network_unavailable()
    {
        var transport = new HttpAuthTransport(new HttpClient(), allowInsecureLoopback: true);

        var result = await transport.GetPolicyAsync(LoopbackServer.ClosedUri());

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorCodes.NetworkUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task Non_json_success_bodies_are_reported_as_invalid_responses()
    {
        await using var server = new LoopbackServer();
        server.Handler = (_, _) => (200, "<html>proxy</html>");

        var result = await Transport().GetPolicyAsync(server.BaseUri);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
    }
}
