using System.Net;

namespace Mosaic.Server.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task Login_returns_tokens_and_me_returns_the_user()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, password, _) = await host.RegisterAsync();

        var login = await host.PostAsync("/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await TestServerHost.JsonAsync(login);
        var access = TestServerHost.AccessToken(session);

        var me = await host.GetAsync("/v1/auth/me", access);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await TestServerHost.JsonAsync(me);
        Assert.Equal(email, body.GetProperty("user").GetProperty("email").GetString());

        var anonymous = await host.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Unknown_account_and_wrong_password_share_the_same_error()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, _, _) = await host.RegisterAsync();

        var wrongPassword = await host.PostAsync("/v1/auth/login", new { email, password = "not the password" });
        var unknown = await host.PostAsync("/v1/auth/login", new
        {
            email = "ghost@example.com",
            password = "not the password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var wrongBody = await TestServerHost.JsonAsync(wrongPassword);
        var unknownBody = await TestServerHost.JsonAsync(unknown);
        Assert.Equal(
            wrongBody.GetProperty("code").GetString(),
            unknownBody.GetProperty("code").GetString());
        Assert.Equal(
            wrongBody.GetProperty("message").GetString(),
            unknownBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Access_token_expires_after_fifteen_minutes()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/v1/auth/me", access)).StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", access)).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_detects_reuse()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var originalRefresh = TestServerHost.RefreshToken(session);

        var refresh = await host.PostAsync("/v1/auth/refresh", new { refreshToken = originalRefresh });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var rotated = await TestServerHost.JsonAsync(refresh);
        var rotatedRefresh = TestServerHost.RefreshToken(rotated);
        var rotatedAccess = TestServerHost.AccessToken(rotated);
        Assert.NotEqual(originalRefresh, rotatedRefresh);

        var reuse = await host.PostAsync("/v1/auth/refresh", new { refreshToken = originalRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("auth.refresh_reused", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(reuse)));

        // Reuse detection revokes the whole family, including the rotated token and access token.
        var afterReuse = await host.PostAsync("/v1/auth/refresh", new { refreshToken = rotatedRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", rotatedAccess)).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_family()
    {
        await using var host = await TestServerHost.StartAsync();
        var (_, _, session) = await host.RegisterAsync();
        var access = TestServerHost.AccessToken(session);
        var refresh = TestServerHost.RefreshToken(session);

        var logout = await host.PostAsync("/v1/auth/logout", new { refreshToken = refresh }, access);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", access)).StatusCode);
        var refreshResponse = await host.PostAsync("/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task Reset_password_revokes_tokens_codes_and_device_authorizations()
    {
        await using var host = await TestServerHost.StartAsync();
        var (email, oldPassword, session) = await host.RegisterAsync();
        var oldAccess = TestServerHost.AccessToken(session);
        var oldRefresh = TestServerHost.RefreshToken(session);

        var deviceStart = await TestServerHost.JsonAsync(
            await host.PostAsync("/v1/device/start", new { deviceLabel = "pending-device" }));
        var deviceCode = deviceStart.GetProperty("deviceCode").GetString()!;
        var deviceUserCode = deviceStart.GetProperty("userCode").GetString()!;
        // Approve but do not poll: the authorization is bound to the user and must be
        // revoked by the password reset before the desktop can ever collect its tokens.
        var approve = await host.PostAsync("/v1/device/approve", new { userCode = deviceUserCode }, oldAccess);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var loginCode = await host.RequestCodeAsync(email, "login");
        var resetCode = await host.RequestCodeAsync(email, "reset");

        const string newPassword = "a completely new passphrase";
        var reset = await host.PostAsync("/v1/auth/reset-password", new
        {
            email,
            code = resetCode,
            newPassword,
        });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", oldAccess)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.PostAsync("/v1/auth/refresh", new { refreshToken = oldRefresh })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/v1/auth/me", oldAccess)).StatusCode);

        var devicePoll = await TestServerHost.JsonAsync(
            await host.PostAsync("/v1/device/poll", new { deviceCode }));
        Assert.Equal("denied", devicePoll.GetProperty("status").GetString());

        var consumedLogin = await host.PostAsync("/v1/auth/login/code", new { email, code = loginCode });
        Assert.Equal(HttpStatusCode.BadRequest, consumedLogin.StatusCode);

        var oldLogin = await host.PostAsync("/v1/auth/login", new { email, password = oldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newLogin = await host.PostAsync("/v1/auth/login", new { email, password = newPassword });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }
}
