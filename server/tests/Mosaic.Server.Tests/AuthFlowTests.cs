using System.Net;

namespace Mosaic.Server.Tests;

public sealed class AuthFlowTests
{
    [Fact]
    public async Task Register_sends_a_six_digit_code_and_returns_a_session()
    {
        await using var host = await TestServerHost.StartAsync();
        const string email = "alice@example.com";
        var code = await host.RequestCodeAsync(email);
        Assert.Matches("^\\d{6}$", code);

        var response = await host.PostAsync("/v1/auth/register", new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code,
            deviceLabel = "unit-test",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await TestServerHost.JsonAsync(response);
        Assert.False(string.IsNullOrEmpty(TestServerHost.AccessToken(session)));
        Assert.False(string.IsNullOrEmpty(TestServerHost.RefreshToken(session)));
        Assert.Equal(email, session.GetProperty("user").GetProperty("email").GetString());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("user").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Verification_code_is_single_use()
    {
        await using var host = await TestServerHost.StartAsync();
        const string email = "single@example.com";
        var code = await host.RequestCodeAsync(email);

        var register = await host.PostAsync("/v1/auth/register", new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code,
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var replay = await host.PostAsync("/v1/auth/login/code", new { email, code });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("auth.invalid_code", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(replay)));
    }

    [Fact]
    public async Task Verification_code_expires_after_ten_minutes()
    {
        await using var host = await TestServerHost.StartAsync();
        const string email = "expired@example.com";
        var code = await host.RequestCodeAsync(email);

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var response = await host.PostAsync("/v1/auth/register", new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.code_expired", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(response)));
    }

    [Fact]
    public async Task Five_wrong_attempts_lock_the_code()
    {
        await using var host = await TestServerHost.StartAsync();
        const string email = "locked@example.com";
        var code = await host.RequestCodeAsync(email);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var response = await host.PostAsync("/v1/auth/register", new
            {
                email,
                password = TestServerHost.DefaultPassword,
                code = wrong,
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var fifth = await host.PostAsync("/v1/auth/register", new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code = wrong,
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal("auth.code_attempts_exceeded", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(fifth)));

        var correct = await host.PostAsync("/v1/auth/register", new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code,
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task Resend_within_sixty_seconds_is_throttled_but_accepted()
    {
        await using var host = await TestServerHost.StartAsync();
        const string email = "resend@example.com";
        await host.RequestCodeAsync(email);
        Assert.Equal(1, host.Email.CountFor(email));

        host.Clock.Advance(TimeSpan.FromSeconds(10));
        var throttle = await host.PostAsync("/v1/auth/email-code", new { email, purpose = "register" });
        Assert.Equal(HttpStatusCode.Accepted, throttle.StatusCode);
        var body = await TestServerHost.JsonAsync(throttle);
        Assert.True(body.GetProperty("resendAfterSeconds").GetInt32() > 0);
        Assert.Equal(1, host.Email.CountFor(email));

        host.Clock.Advance(TimeSpan.FromSeconds(51));
        var resend = await host.PostAsync("/v1/auth/email-code", new { email, purpose = "register" });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        Assert.Equal(2, host.Email.CountFor(email));
    }

    [Fact]
    public async Task Email_code_response_is_identical_for_known_and_unknown_addresses()
    {
        await using var host = await TestServerHost.StartAsync();
        var (known, _, _) = await host.RegisterAsync();

        var knownResponse = await host.PostAsync("/v1/auth/email-code", new { email = known, purpose = "login" });
        var unknownResponse = await host.PostAsync(
            "/v1/auth/email-code",
            new { email = "ghost@example.com", purpose = "login" });

        Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
        var knownBody = await TestServerHost.JsonAsync(knownResponse);
        var unknownBody = await TestServerHost.JsonAsync(unknownResponse);
        Assert.Equal(knownBody.GetProperty("status").GetString(), unknownBody.GetProperty("status").GetString());
        Assert.Equal(
            knownBody.GetProperty("resendAfterSeconds").GetInt32(),
            unknownBody.GetProperty("resendAfterSeconds").GetInt32());
        Assert.Equal(0, host.Email.CountFor("ghost@example.com"));
    }

    [Fact]
    public async Task Forgot_password_is_uniform_for_known_and_unknown_addresses()
    {
        await using var host = await TestServerHost.StartAsync();
        var (known, _, _) = await host.RegisterAsync();
        var before = host.Email.CountFor(known);

        var knownResponse = await host.PostAsync("/v1/auth/forgot-password", new { email = known });
        var unknownResponse = await host.PostAsync("/v1/auth/forgot-password", new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
        Assert.Equal(before + 1, host.Email.CountFor(known));
        Assert.Equal(0, host.Email.CountFor("nobody@example.com"));
    }

    [Fact]
    public async Task Email_requests_return_a_uniform_503_when_smtp_is_not_configured()
    {
        await using var host = await TestServerHost.StartAsync();
        var (known, _, _) = await host.RegisterAsync();
        // Remove the effective mail configuration: both known and unknown addresses
        // must see the same 503 instead of an account-specific result.
        host.Options.Email = new Mosaic.Server.Emailing.EmailSettings();

        var knownResponse = await host.PostAsync("/v1/auth/email-code", new { email = known, purpose = "login" });
        var unknownResponse = await host.PostAsync(
            "/v1/auth/email-code",
            new { email = "ghost@example.com", purpose = "login" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, knownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknownResponse.StatusCode);
        Assert.Equal("email.not_configured", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(unknownResponse)));
    }

    [Fact]
    public async Task Ip_rate_limit_caps_code_requests()
    {
        await using var host = await TestServerHost.StartAsync(options => options.EmailIpLimitPerWindow = 3);
        for (var index = 0; index < 3; index++)
        {
            var response = await host.PostAsync("/v1/auth/email-code", new
            {
                email = $"limit-{index}@example.com",
                purpose = "login",
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var blocked = await host.PostAsync("/v1/auth/email-code", new
        {
            email = "limit-4@example.com",
            purpose = "login",
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("auth.rate_limited", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(blocked)));
    }

    [Fact]
    public async Task Per_address_hourly_limit_caps_code_requests()
    {
        await using var host = await TestServerHost.StartAsync(options =>
        {
            options.EmailAddressLimitPerHour = 2;
            options.EmailCodeResendSeconds = 1;
        });
        const string email = "hourly@example.com";
        for (var index = 0; index < 2; index++)
        {
            var response = await host.PostAsync("/v1/auth/email-code", new { email, purpose = "register" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            host.Clock.Advance(TimeSpan.FromSeconds(2));
        }

        var blocked = await host.PostAsync("/v1/auth/email-code", new { email, purpose = "register" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task Concurrent_registration_with_one_code_creates_one_account()
    {
        await using var host = await TestServerHost.StartAsync();
        const string email = "race@example.com";
        var code = await host.RequestCodeAsync(email);
        var body = new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code,
        };

        var results = await Task.WhenAll(
            host.PostAsync("/v1/auth/register", body),
            host.PostAsync("/v1/auth/register", body));

        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, response => response.StatusCode != HttpStatusCode.Created);

        var login = await host.PostAsync("/v1/auth/login", new { email, password = TestServerHost.DefaultPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Registration_closed_is_enforced_without_sending_a_code()
    {
        await using var host = await TestServerHost.StartAsync(options =>
            options.RegistrationMode = RegistrationMode.Closed);
        const string email = "closed@example.com";

        var codeResponse = await host.PostAsync("/v1/auth/email-code", new { email, purpose = "register" });
        Assert.Equal(HttpStatusCode.Accepted, codeResponse.StatusCode);
        Assert.Equal(0, host.Email.CountFor(email));

        var register = await host.PostAsync("/v1/auth/register", new
        {
            email,
            password = TestServerHost.DefaultPassword,
            code = "123456",
        });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Equal("auth.registration_closed", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(register)));

        var policy = await TestServerHost.JsonAsync(await host.GetAsync("/v1/auth/policy"));
        Assert.False(policy.GetProperty("registrationOpen").GetBoolean());
    }

    [Fact]
    public async Task Weak_passwords_and_invalid_emails_are_rejected()
    {
        await using var host = await TestServerHost.StartAsync();

        var weak = await host.PostAsync("/v1/auth/register", new
        {
            email = "weak@example.com",
            password = "short",
            code = "123456",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        Assert.Equal("auth.weak_password", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(weak)));

        var invalidEmail = await host.PostAsync("/v1/auth/register", new
        {
            email = "not-an-email",
            password = TestServerHost.DefaultPassword,
            code = "123456",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidEmail.StatusCode);
        Assert.Equal("auth.invalid_email", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(invalidEmail)));
    }

    [Fact]
    public async Task Errors_include_code_message_trace_id_and_header()
    {
        await using var host = await TestServerHost.StartAsync();
        var response = await host.PostAsync("/v1/auth/register", new
        {
            email = "broken",
            password = TestServerHost.DefaultPassword,
            code = "123456",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.Headers.GetValues("X-Trace-Id").Single()));
        var body = await TestServerHost.JsonAsync(response);
        Assert.Equal("auth.invalid_email", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Malformed_json_is_a_400_request_error()
    {
        await using var host = await TestServerHost.StartAsync();
        using var content = new StringContent("{oops", System.Text.Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync("/v1/auth/login", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.invalid", TestServerHost.ErrorCode(await TestServerHost.JsonAsync(response)));
    }
}
