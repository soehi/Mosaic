using System.Net;

namespace Mosaic.Server.Tests;

public sealed class DeviceFlowTests
{
    [Fact]
    public async Task Device_flow_issues_a_session_exactly_once()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);

        var startResponse = await host.PostAsync("/v1/device/start", new { deviceLabel = "Mosaic Desktop 0.15.3" });
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var start = await TestServerHost.JsonAsync(startResponse);
        var deviceCode = start.GetProperty("deviceCode").GetString()!;
        var userCode = start.GetProperty("userCode").GetString()!;
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}$", userCode);
        Assert.Equal(600, start.GetProperty("expiresIn").GetInt64());
        Assert.Equal(2, start.GetProperty("interval").GetInt32());
        Assert.Equal("http://127.0.0.1/device", start.GetProperty("verificationUri").GetString());
        Assert.Contains(userCode, start.GetProperty("verificationUriComplete").GetString(), StringComparison.Ordinal);

        var pending = await TestServerHost.JsonAsync(
            await host.PostAsync("/v1/device/poll", new { deviceCode }));
        Assert.Equal("pending", pending.GetProperty("status").GetString());

        var approve = await host.PostAsync("/v1/device/approve", new { userCode }, access);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var approvedResponse = await host.PostAsync("/v1/device/poll", new { deviceCode });
        var approved = await TestServerHost.JsonAsync(approvedResponse);
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        var deviceSession = approved.GetProperty("session");
        Assert.Equal(email, deviceSession.GetProperty("user").GetProperty("email").GetString());
        var deviceAccess = TestServerHost.AccessToken(deviceSession);

        var me = await host.GetAsync("/v1/auth/me", deviceAccess);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var second = await TestServerHost.JsonAsync(
            await host.PostAsync("/v1/device/poll", new { deviceCode }));
        Assert.Equal("consumed", second.GetProperty("status").GetString());
        Assert.False(second.TryGetProperty("session", out _));
    }

    [Fact]
    public async Task Denied_authorization_is_reported_as_denied()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);
        var start = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/start", new { }));

        var deny = await host.PostAsync(
            "/v1/device/deny",
            new { userCode = start.GetProperty("userCode").GetString() },
            access);
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);

        var poll = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/poll", new
        {
            deviceCode = start.GetProperty("deviceCode").GetString(),
        }));
        Assert.Equal("denied", poll.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Expired_device_codes_report_expired_and_cannot_be_approved()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);
        var start = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/start", new { }));

        host.Clock.Advance(TimeSpan.FromMinutes(11));

        var poll = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/poll", new
        {
            deviceCode = start.GetProperty("deviceCode").GetString(),
        }));
        Assert.Equal("expired", poll.GetProperty("status").GetString());

        var approve = await host.PostAsync(
            "/v1/device/approve",
            new { userCode = start.GetProperty("userCode").GetString() },
            access);
        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
    }

    [Fact]
    public async Task Approval_requires_a_logged_in_user()
    {
        await using var host = await TestServerHost.StartAsync();
        var start = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/start", new { }));

        var approve = await host.PostAsync(
            "/v1/device/approve",
            new { userCode = start.GetProperty("userCode").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, approve.StatusCode);
    }

    [Fact]
    public async Task Unknown_user_codes_are_rejected()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);

        var approve = await host.PostAsync("/v1/device/approve", new { userCode = "BCDF-GHJK" }, access);
        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);

        var malformed = await host.PostAsync("/v1/device/poll", new { deviceCode = "not-a-real-code" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Device_start_accepts_an_empty_body()
    {
        await using var host = await TestServerHost.StartAsync();
        var response = await host.SendAsync(HttpMethod.Post, "/v1/device/start");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestServerHost.JsonAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("deviceCode").GetString()));
    }

    [Fact]
    public async Task Device_start_returns_metadata_complete_uri_and_info()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);

        var startResponse = await host.PostAsync("/v1/device/start", new
        {
            deviceLabel = "Mosaic Desktop",
            platform = "windows",
            appVersion = "0.16.0",
        });
        var start = await TestServerHost.JsonAsync(startResponse);
        var userCode = start.GetProperty("userCode").GetString()!;
        var complete = start.GetProperty("verificationUriComplete").GetString()!;
        Assert.Equal(start.GetProperty("verificationUri").GetString(), complete[..complete.IndexOf('?')]);
        Assert.Contains(Uri.EscapeDataString(userCode), complete, StringComparison.Ordinal);

        var infoResponse = await host.GetAsync(
            "/v1/device/info?userCode=" + Uri.EscapeDataString(userCode),
            access);
        Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode);
        var info = await TestServerHost.JsonAsync(infoResponse);
        Assert.Equal("Mosaic Desktop", info.GetProperty("deviceLabel").GetString());
        Assert.Equal("windows", info.GetProperty("platform").GetString());
        Assert.Equal("0.16.0", info.GetProperty("appVersion").GetString());
        Assert.Equal("pending", info.GetProperty("state").GetString());

        var unknown = await host.GetAsync("/v1/device/info?userCode=BCDF-GHJK", access);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var anonymous = await host.GetAsync("/v1/device/info?userCode=" + Uri.EscapeDataString(userCode));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Polling_faster_than_the_interval_answers_slow_down()
    {
        await using var host = await TestServerHost.StartAsync();
        var start = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/start", new { }));
        var deviceCode = start.GetProperty("deviceCode").GetString();

        var first = await host.PostAsync("/v1/device/poll", new { deviceCode });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var tooFast = await host.PostAsync("/v1/device/poll", new { deviceCode });
        Assert.Equal(HttpStatusCode.OK, tooFast.StatusCode);
        var tooFastBody = await TestServerHost.JsonAsync(tooFast);
        Assert.Equal("slow_down", tooFastBody.GetProperty("status").GetString());
        Assert.Equal(2, tooFastBody.GetProperty("interval").GetInt32());
        Assert.False(tooFastBody.TryGetProperty("session", out _));

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await TestServerHost.JsonAsync(await host.PostAsync("/v1/device/poll", new { deviceCode }));
        Assert.Equal("pending", second.GetProperty("status").GetString());
        Assert.Equal(2, second.GetProperty("interval").GetInt32());
    }

    [Fact]
    public async Task Device_page_is_served_with_security_headers()
    {
        await using var host = await TestServerHost.StartAsync();
        var response = await host.GetAsync("/device");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("授权码", html, StringComparison.Ordinal);
        Assert.Contains("/v1/device/approve", html, StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(),
            StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
