using System.Globalization;
using Mosaic.Server.Data;
using Mosaic.Server.Emailing;
using Mosaic.Server.Http;
using Mosaic.Server.RateLimiting;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public sealed record CodeRequestResult(int ResendAfterSeconds);

/// <summary>
/// Six-digit mailbox codes. Rows store only a peppered HMAC of the code and an
/// HMAC of the address; consumption is a conditional update so exactly one of
/// any concurrent verifications can succeed.
/// </summary>
public sealed class EmailCodeService(
    Database database,
    ServerOptions options,
    SecretHasher hasher,
    EmailSettingsStore mailSettings,
    IEmailSender emailSender,
    WindowLimiter limiter,
    TimeProvider timeProvider)
{
    public const string PurposeRegister = "register";

    public const string PurposeLogin = "login";

    public const string PurposeReset = "reset";

    public static string NormalizePurpose(string? purpose) => purpose?.Trim().ToLowerInvariant() switch
    {
        PurposeRegister => PurposeRegister,
        PurposeLogin => PurposeLogin,
        PurposeReset => PurposeReset,
        _ => throw new ApiException(422, "request.invalid", "验证码用途无效。"),
    };

    public async Task<CodeRequestResult> RequestAsync(
        string? rawEmail,
        string? purpose,
        string ipHash,
        CancellationToken cancellationToken)
    {
        var normalizedPurpose = NormalizePurpose(purpose);
        var email = EmailNormalizer.Normalize(rawEmail);
        var emailHash = hasher.EmailHash(email);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        if (!limiter.TryAcquire(
                "email-ip:" + ipHash,
                options.EmailIpLimitPerWindow,
                TimeSpan.FromSeconds(options.EmailIpWindowSeconds)))
        {
            throw new ApiException(429, "auth.rate_limited", "请求过于频繁，请稍后再试。",
                retryAfterSeconds: options.EmailIpWindowSeconds);
        }

        // Uniform 503 regardless of whether the address exists: never let SMTP
        // availability become an account oracle.
        var settings = await mailSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsConfigured)
        {
            throw new ApiException(503, "email.not_configured", "服务端尚未配置邮件发送，暂时无法使用邮箱验证码。");
        }

        var code = Tokens.CreateEmailCode();
        var codeHash = hasher.CodeHash(emailHash, normalizedPurpose, code);
        var codeId = Guid.NewGuid().ToString("N");

        var decision = await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var recent = await connection.ScalarInt64Async(
                    transaction,
                    """
                    SELECT COUNT(*) FROM email_codes
                    WHERE email_hash = $hash AND purpose = $purpose AND created_at > $since
                    """,
                    token,
                    ("$hash", emailHash),
                    ("$purpose", normalizedPurpose),
                    ("$since", now - 3600)).ConfigureAwait(false);
                if (recent >= options.EmailAddressLimitPerHour)
                {
                    throw new ApiException(429, "auth.rate_limited", "请求过于频繁，请稍后再试。",
                        retryAfterSeconds: 3600);
                }

                var latestSent = await connection.ScalarAsync(
                    transaction,
                    """
                    SELECT MAX(last_sent_at) FROM email_codes
                    WHERE email_hash = $hash AND purpose = $purpose
                    """,
                    token,
                    ("$hash", emailHash),
                    ("$purpose", normalizedPurpose)).ConfigureAwait(false);
                if (latestSent is not null)
                {
                    var elapsed = now - Convert.ToInt64(latestSent, CultureInfo.InvariantCulture);
                    if (elapsed < options.EmailCodeResendSeconds)
                    {
                        return new Decision(
                            false,
                            (int)Math.Max(1, options.EmailCodeResendSeconds - elapsed));
                    }
                }

                var isRegistered = await connection.ScalarInt64Async(
                    transaction,
                    "SELECT COUNT(*) FROM users WHERE email = $email",
                    token,
                    ("$email", email)).ConfigureAwait(false) > 0;
                var shouldSend = normalizedPurpose == PurposeRegister
                    ? !isRegistered && options.RegistrationMode == RegistrationMode.Open
                    : isRegistered;

                await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE email_codes SET consumed_at = $now
                    WHERE email_hash = $hash AND purpose = $purpose AND consumed_at IS NULL
                    """,
                    token,
                    ("$now", now),
                    ("$hash", emailHash),
                    ("$purpose", normalizedPurpose)).ConfigureAwait(false);

                await connection.ExecuteAsync(
                    transaction,
                    """
                    INSERT INTO email_codes(
                        id, email_hash, purpose, code_hash, payload_json, expires_at,
                        attempts, consumed_at, last_sent_at, created_at)
                    VALUES($id, $hash, $purpose, $code, '{}', $expires, 0, NULL, $now, $now)
                    """,
                    token,
                    ("$id", codeId),
                    ("$hash", emailHash),
                    ("$purpose", normalizedPurpose),
                    ("$code", codeHash),
                    ("$expires", now + options.EmailCodeTtlSeconds),
                    ("$now", now)).ConfigureAwait(false);

                return new Decision(shouldSend, options.EmailCodeResendSeconds);
            },
            cancellationToken).ConfigureAwait(false);

        if (!decision.Send)
        {
            return new CodeRequestResult(decision.ResendAfterSeconds);
        }

        var message = EmailTemplates.Code(options.ServerName, code, options.EmailCodeTtlSeconds) with { To = email };
        try
        {
            await emailSender.SendAsync(message, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (EmailUnavailableException exception)
        {
            await database.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await connection.ExecuteAsync(
                        transaction,
                        "DELETE FROM email_codes WHERE id = $id AND consumed_at IS NULL",
                        token,
                        ("$id", codeId)).ConfigureAwait(false);
                    return 0;
                },
                CancellationToken.None).ConfigureAwait(false);
            throw new ApiException(503, exception.Code, "邮件发送失败，请稍后重试。");
        }

        return new CodeRequestResult(decision.ResendAfterSeconds);
    }

    /// <summary>
    /// Verifies and consumes the newest active code. Returns the stored payload
    /// JSON (currently "{}"). Every failure keeps a uniform message so callers
    /// cannot learn whether an account exists.
    /// </summary>
    public async Task<string> ConsumeAsync(
        string? rawEmail,
        string purpose,
        string? code,
        CancellationToken cancellationToken)
    {
        var normalizedPurpose = NormalizePurpose(purpose);
        var email = EmailNormalizer.Normalize(rawEmail);
        if (code is null || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            throw InvalidCode();
        }

        var emailHash = hasher.EmailHash(email);
        var expectedHash = hasher.CodeHash(emailHash, normalizedPurpose, code);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        var result = await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var row = await connection.SingleAsync(
                    transaction,
                    """
                    SELECT id, code_hash, payload_json, expires_at, attempts
                    FROM email_codes
                    WHERE email_hash = $hash AND purpose = $purpose AND consumed_at IS NULL
                    ORDER BY created_at DESC LIMIT 1
                    """,
                    reader => new CodeRow(
                        reader.Str("id"),
                        reader.Str("code_hash"),
                        reader.Str("payload_json"),
                        reader.Long("expires_at"),
                        reader.Long("attempts")),
                    token,
                    ("$hash", emailHash),
                    ("$purpose", normalizedPurpose)).ConfigureAwait(false);

                if (row is null)
                {
                    return Rejection(400, "auth.invalid_code", "验证码无效或已过期。");
                }

                if (row.ExpiresAt <= now)
                {
                    await connection.ExecuteAsync(
                        transaction,
                        "UPDATE email_codes SET consumed_at = $now WHERE id = $id AND consumed_at IS NULL",
                        token,
                        ("$now", now),
                        ("$id", row.Id)).ConfigureAwait(false);
                    return Rejection(400, "auth.code_expired", "验证码已过期，请重新获取。");
                }

                if (row.Attempts >= options.EmailCodeMaxAttempts)
                {
                    return Rejection(429, "auth.code_attempts_exceeded", "验证码尝试次数过多，请重新获取。");
                }

                if (!hasher.FixedTimeEquals(row.CodeHash, expectedHash))
                {
                    await connection.ExecuteAsync(
                        transaction,
                        "UPDATE email_codes SET attempts = attempts + 1 WHERE id = $id AND consumed_at IS NULL",
                        token,
                        ("$id", row.Id)).ConfigureAwait(false);
                    return row.Attempts + 1 >= options.EmailCodeMaxAttempts
                        ? Rejection(429, "auth.code_attempts_exceeded", "验证码尝试次数过多，请重新获取。")
                        : Rejection(400, "auth.invalid_code", "验证码无效或已过期。");
                }

                var consumed = await connection.ExecuteAsync(
                    transaction,
                    "UPDATE email_codes SET consumed_at = $now WHERE id = $id AND consumed_at IS NULL",
                    token,
                    ("$now", now),
                    ("$id", row.Id)).ConfigureAwait(false);
                return consumed == 1
                    ? new ConsumeOutcome(true, row.Payload, 0, string.Empty, string.Empty, null)
                    : Rejection(400, "auth.invalid_code", "验证码无效或已过期。");
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
        {
            throw new ApiException(result.Status, result.Code, result.Message,
                retryAfterSeconds: result.RetryAfterSeconds);
        }

        return result.Payload ?? "{}";
    }

    private static ConsumeOutcome Rejection(int status, string code, string message, int? retryAfter = null) =>
        new(false, null, status, code, message, retryAfter);

    private static ApiException InvalidCode() =>
        new(400, "auth.invalid_code", "验证码无效或已过期。");

    private sealed record Decision(bool Send, int ResendAfterSeconds);

    private sealed record CodeRow(string Id, string CodeHash, string Payload, long ExpiresAt, long Attempts);

    private sealed record ConsumeOutcome(
        bool Ok,
        string? Payload,
        int Status,
        string Code,
        string Message,
        int? RetryAfterSeconds);
}
