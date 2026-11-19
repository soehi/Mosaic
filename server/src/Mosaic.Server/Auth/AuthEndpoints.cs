using System.Diagnostics;
using Mosaic.Server.Emailing;
using Mosaic.Server.Http;
using Mosaic.Server.RateLimiting;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public static class AuthEndpoints
{
    private static readonly (byte[] Salt, byte[] Hash) DummyPassword =
        PasswordHasher.Hash("mosaic-dummy-password-value");

    public static void MapAuthEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mosaic.Server.Auth");

        app.MapGet("/v1/auth/policy", async (
            ServerOptions options,
            EmailSettingsStore mail,
            CancellationToken cancellationToken) =>
        {
            var settings = await mail.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new
            {
                serverName = options.ServerName,
                registrationOpen = options.RegistrationMode == RegistrationMode.Open,
                requireEmailVerification = true,
                emailAvailable = settings.IsConfigured,
                codeLength = 6,
                codeTtlSeconds = options.EmailCodeTtlSeconds,
                resendAfterSeconds = options.EmailCodeResendSeconds,
                passwordMinLength = PasswordPolicy.MinLength,
                passwordMaxLength = PasswordPolicy.MaxLength,
            });
        });

        app.MapPost("/v1/auth/email-code", async (
            EmailCodeRequest body,
            HttpContext http,
            EmailCodeService codes,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "code.request", ipHash, null, async () =>
            {
                var result = await codes.RequestAsync(body.Email, body.Purpose, ipHash, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(
                    new { status = "accepted", resendAfterSeconds = result.ResendAfterSeconds },
                    statusCode: 202);
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/register", async (
            RegisterRequest body,
            HttpContext http,
            ServerOptions options,
            EmailCodeService codes,
            SessionService sessions,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "register", ipHash, null, async () =>
            {
                if (options.RegistrationMode != RegistrationMode.Open)
                {
                    throw new ApiException(403, "auth.registration_closed", "服务端已关闭注册。");
                }

                var email = EmailNormalizer.Normalize(body.Email);
                PasswordPolicy.Validate(body.Password);
                await codes.ConsumeAsync(email, EmailCodeService.PurposeRegister, body.Code, cancellationToken)
                    .ConfigureAwait(false);
                var user = await sessions.CreateUserAsync(email, body.Password!, cancellationToken)
                    .ConfigureAwait(false);
                var tokens = await sessions.CreateAsync(
                    user,
                    AuthSupport.SanitizeDeviceLabel(body.DeviceLabel),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(AuthSupport.SessionJson(tokens), statusCode: 201);
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/login", async (
            LoginRequest body,
            HttpContext http,
            ServerOptions options,
            SessionService sessions,
            SecretHasher hasher,
            WindowLimiter limiter,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "login.password", ipHash, null, async () =>
            {
                var email = EmailNormalizer.Normalize(body.Email);
                var window = TimeSpan.FromSeconds(options.LoginIpWindowSeconds);
                if (!limiter.TryAcquire("login-ip:" + ipHash, options.LoginIpLimitPerWindow, window))
                {
                    throw new ApiException(429, "auth.rate_limited", "请求过于频繁，请稍后再试。",
                        retryAfterSeconds: options.LoginIpWindowSeconds);
                }

                var emailHash = hasher.EmailHash(email);
                var failureKey = "login-fail:" + emailHash;
                var failureWindow = TimeSpan.FromSeconds(options.LoginFailureWindowSeconds);
                if (limiter.IsLimited(failureKey, options.LoginFailureLimit, failureWindow))
                {
                    throw new ApiException(429, "auth.rate_limited", "尝试次数过多，请稍后再试。",
                        retryAfterSeconds: options.LoginFailureWindowSeconds);
                }

                var password = body.Password ?? string.Empty;
                var credentials = await sessions.FindCredentialsAsync(email, cancellationToken)
                    .ConfigureAwait(false);
                if (credentials is null)
                {
                    // Burn the same PBKDF2 cost so unknown accounts are not faster.
                    PasswordHasher.Verify(password, DummyPassword.Salt, DummyPassword.Hash);
                    limiter.TryAcquire(failureKey, options.LoginFailureLimit, failureWindow);
                    throw InvalidCredentials();
                }

                if (!PasswordHasher.Verify(password, credentials.Salt, credentials.Hash))
                {
                    limiter.TryAcquire(failureKey, options.LoginFailureLimit, failureWindow);
                    throw InvalidCredentials();
                }

                var tokens = await sessions.CreateAsync(
                    credentials.User,
                    AuthSupport.SanitizeDeviceLabel(body.DeviceLabel),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(AuthSupport.SessionJson(tokens));
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/login/code", async (
            CodeLoginRequest body,
            HttpContext http,
            ServerOptions options,
            EmailCodeService codes,
            SessionService sessions,
            SecretHasher hasher,
            WindowLimiter limiter,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "login.code", ipHash, null, async () =>
            {
                var email = EmailNormalizer.Normalize(body.Email);
                if (!limiter.TryAcquire(
                        "login-code-ip:" + ipHash,
                        options.LoginIpLimitPerWindow,
                        TimeSpan.FromSeconds(options.LoginIpWindowSeconds)))
                {
                    throw new ApiException(429, "auth.rate_limited", "请求过于频繁，请稍后再试。",
                        retryAfterSeconds: options.LoginIpWindowSeconds);
                }

                await codes.ConsumeAsync(email, EmailCodeService.PurposeLogin, body.Code, cancellationToken)
                    .ConfigureAwait(false);
                var user = await sessions.FindUserByEmailAsync(email, cancellationToken).ConfigureAwait(false)
                    ?? throw InvalidCredentials();
                var tokens = await sessions.CreateAsync(
                    user,
                    AuthSupport.SanitizeDeviceLabel(body.DeviceLabel),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(AuthSupport.SessionJson(tokens));
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/refresh", async (
            RefreshRequest body,
            HttpContext http,
            SessionService sessions,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "session.refresh", ipHash, null, async () =>
            {
                if (string.IsNullOrWhiteSpace(body.RefreshToken))
                {
                    throw new ApiException(422, "request.invalid", "缺少 refreshToken。");
                }

                var tokens = await sessions.RefreshAsync(body.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AuthSupport.SessionJson(tokens));
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/logout", async (
            LogoutRequest body,
            HttpContext http,
            SessionService sessions,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            return await TracedAsync(logger, "session.logout", ipHash, user.Id, async () =>
            {
                if (string.IsNullOrWhiteSpace(body.RefreshToken))
                {
                    throw new ApiException(422, "request.invalid", "缺少 refreshToken。");
                }

                await sessions.LogoutAsync(user.Id, body.RefreshToken, body.AllDevices == true, cancellationToken)
                    .ConfigureAwait(false);
                return Results.NoContent();
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/forgot-password", async (
            ForgotPasswordRequest body,
            HttpContext http,
            EmailCodeService codes,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "password.forgot", ipHash, null, async () =>
            {
                var result = await codes.RequestAsync(
                    body.Email,
                    EmailCodeService.PurposeReset,
                    ipHash,
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(
                    new { status = "accepted", resendAfterSeconds = result.ResendAfterSeconds },
                    statusCode: 202);
            }).ConfigureAwait(false);
        });

        app.MapPost("/v1/auth/reset-password", async (
            ResetPasswordRequest body,
            HttpContext http,
            EmailCodeService codes,
            SessionService sessions,
            SecretHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            return await TracedAsync(logger, "password.reset", ipHash, null, async () =>
            {
                var email = EmailNormalizer.Normalize(body.Email);
                PasswordPolicy.Validate(body.NewPassword);
                await codes.ConsumeAsync(email, EmailCodeService.PurposeReset, body.Code, cancellationToken)
                    .ConfigureAwait(false);
                var user = await sessions.FindUserByEmailAsync(email, cancellationToken).ConfigureAwait(false)
                    ?? throw InvalidCredentials();
                await sessions.UpdatePasswordAsync(user.Id, body.NewPassword!, cancellationToken)
                    .ConfigureAwait(false);
                await sessions.RevokeAllAsync(user.Id, user.Email, cancellationToken).ConfigureAwait(false);
                return Results.Json(new { status = "password_reset" });
            }).ConfigureAwait(false);
        });

        app.MapGet("/v1/auth/me", async (
            HttpContext http,
            SessionService sessions,
            SecretHasher hasher) =>
        {
            var ipHash = AuthSupport.IpHash(http, hasher);
            var user = await AuthSupport.RequireUserAsync(http, sessions).ConfigureAwait(false);
            AuthSupport.LogEvent(logger, "session.me", "ok", ipHash, Stopwatch.GetTimestamp(), user.Id);
            return Results.Json(new { user = AuthSupport.UserJson(user) });
        });
    }

    private static async Task<IResult> TracedAsync(
        ILogger logger,
        string action,
        string ipHash,
        string? userId,
        Func<Task<IResult>> work)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await work().ConfigureAwait(false);
            AuthSupport.LogEvent(logger, action, "ok", ipHash, started, userId);
            return result;
        }
        catch (ApiException exception)
        {
            AuthSupport.LogEvent(logger, action, exception.Code, ipHash, started, userId, exception.Status);
            throw;
        }
    }

    private static ApiException InvalidCredentials() =>
        new(401, "auth.invalid_credentials", "邮箱或密码不正确。");
}
