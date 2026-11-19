using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mosaic.Server.Data;
using Mosaic.Server.Http;
using Mosaic.Server.RateLimiting;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public sealed record DeviceStartResult(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    long ExpiresIn,
    int Interval);

public sealed record DevicePollResult(string Status, SessionTokens? Session, int Interval = 2);

public sealed record DeviceInfoResult(
    string? DeviceLabel,
    string? Platform,
    string? AppVersion,
    string State,
    long ExpiresAt,
    long? ApprovedAt);

/// <summary>
/// OAuth-style device authorization: the desktop shows an eight-character code,
/// the user signs in through the browser page and approves it, and the desktop
/// polls exactly once for the issued session.
/// </summary>
public sealed class DeviceFlowService(
    Database database,
    ServerOptions options,
    SessionService sessions,
    SecretProtector protector,
    SecretHasher hasher,
    WindowLimiter limiter,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DeviceStartResult> StartAsync(
        string? requestedLabel,
        string? requestedPlatform,
        string? requestedAppVersion,
        string ipHash,
        CancellationToken cancellationToken)
    {
        if (!limiter.TryAcquire("device-start:" + ipHash, 30, TimeSpan.FromMinutes(10)))
        {
            throw new ApiException(429, "device.rate_limited", "请求过于频繁，请稍后再试。", retryAfterSeconds: 600);
        }

        var now = Now();
        var expiresAt = now + options.DeviceCodeTtlSeconds;
        var label = AuthSupport.SanitizeDeviceLabel(requestedLabel);
        var platform = AuthSupport.SanitizeShort(requestedPlatform);
        var appVersion = AuthSupport.SanitizeShort(requestedAppVersion);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var deviceCode = Tokens.Create();
            var userCode = Tokens.CreateUserCode();
            var compact = Tokens.NormalizeUserCode(userCode)!;
            try
            {
                await database.WriteAsync(
                    async (connection, transaction, token) =>
                    {
                        await connection.ExecuteAsync(
                            transaction,
                            """
                            INSERT INTO device_auth(
                                id, user_code_hash, device_code_hash, state, device_label,
                                platform, app_version, created_at, expires_at)
                            VALUES($id, $user, $device, 'pending', $label, $platform, $version, $now, $expires)
                            """,
                            token,
                            ("$id", Guid.NewGuid().ToString("N")),
                            ("$user", hasher.UserCodeHash(compact)),
                            ("$device", hasher.DeviceCodeHash(deviceCode)),
                            ("$label", label),
                            ("$platform", platform),
                            ("$version", appVersion),
                            ("$now", now),
                            ("$expires", expiresAt)).ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                continue;
            }

            var verificationUri = options.PublicUrl + "/device";
            return new DeviceStartResult(
                deviceCode,
                userCode,
                verificationUri,
                verificationUri + "?code=" + Uri.EscapeDataString(userCode),
                options.DeviceCodeTtlSeconds,
                Two);
        }

        throw new ApiException(503, "server.error", "暂时无法创建授权码，请稍后重试。");
    }

    public async Task<DeviceInfoResult> InfoAsync(SessionUser user, string? rawUserCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var compact = Tokens.NormalizeUserCode(rawUserCode)
            ?? throw new ApiException(404, "device.invalid_code", "授权码不存在或已过期。");
        var userCodeHash = hasher.UserCodeHash(compact);

        var row = await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                """
                SELECT user_id, state, device_label, platform, app_version, expires_at, approved_at
                FROM device_auth WHERE user_code_hash = $hash
                """,
                reader => new InfoRow(
                    reader.StrOrNull("user_id"),
                    reader.Str("state"),
                    reader.Str("device_label"),
                    reader.Str("platform"),
                    reader.Str("app_version"),
                    reader.Long("expires_at"),
                    reader.LongOrNull("approved_at")),
                token,
                ("$hash", userCodeHash)).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (row is null || (row.UserId is not null && row.UserId != user.Id) || row.ExpiresAt <= Now())
        {
            throw new ApiException(404, "device.invalid_code", "授权码不存在或已过期。");
        }

        return new DeviceInfoResult(
            row.DeviceLabel,
            row.Platform,
            row.AppVersion,
            row.State,
            row.ExpiresAt,
            row.ApprovedAt);
    }

    public async Task<DevicePollResult> PollAsync(string? rawDeviceCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawDeviceCode) || rawDeviceCode.Length > 200)
        {
            throw new ApiException(400, "device.invalid_code", "授权码无效。");
        }

        var deviceCodeHash = hasher.DeviceCodeHash(rawDeviceCode.Trim());
        if (!limiter.TryAcquire("device-poll:" + deviceCodeHash, 60, TimeSpan.FromMinutes(1)))
        {
            throw new ApiException(429, "device.rate_limited", "请求过于频繁，请稍后再试。", retryAfterSeconds: 60);
        }

        return await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Now();
                var row = await connection.SingleAsync(
                    transaction,
                    """
                    SELECT id, state, expires_at, secret_cipher, last_polled_at
                    FROM device_auth WHERE device_code_hash = $hash
                    """,
                    reader => new DeviceRow(
                        reader.Str("id"),
                        reader.Str("state"),
                        reader.Long("expires_at"),
                        reader.StrOrNull("secret_cipher"),
                        reader.LongOrNull("last_polled_at")),
                    token,
                    ("$hash", deviceCodeHash)).ConfigureAwait(false);

                if (row is null)
                {
                    throw new ApiException(400, "device.invalid_code", "授权码无效。");
                }

                if (row.State is "pending" && row.LastPolledAt is { } lastPolled && now - lastPolled < Two)
                {
                    // RFC 8628 slow_down: the client polls faster than the advertised interval.
                    throw new ApiException(429, "slow_down", "轮询过快，请放慢轮询频率。", retryAfterSeconds: Two);
                }

                if (row.State is "consumed")
                {
                    return new DevicePollResult("consumed", null, Two);
                }

                if (row.State is "denied")
                {
                    return new DevicePollResult("denied", null, Two);
                }

                if (row.ExpiresAt <= now)
                {
                    if (row.State is "pending")
                    {
                        await connection.ExecuteAsync(
                            transaction,
                            "UPDATE device_auth SET state = 'expired' WHERE id = $id AND state = 'pending'",
                            token,
                            ("$id", row.Id)).ConfigureAwait(false);
                    }

                    return new DevicePollResult("expired", null, Two);
                }

                if (row.State is "pending")
                {
                    await connection.ExecuteAsync(
                        transaction,
                        "UPDATE device_auth SET last_polled_at = $now WHERE id = $id",
                        token,
                        ("$now", now),
                        ("$id", row.Id)).ConfigureAwait(false);
                    return new DevicePollResult("pending", null, Two);
                }

                var consumed = await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE device_auth SET state = 'consumed', consumed_at = $now, secret_cipher = NULL
                    WHERE id = $id AND state = 'approved'
                    """,
                    token,
                    ("$now", now),
                    ("$id", row.Id)).ConfigureAwait(false);
                if (consumed != 1)
                {
                    return new DevicePollResult("consumed", null, Two);
                }

                if (row.SecretCipher is null)
                {
                    throw new ApiException(500, "server.error", "设备授权状态损坏，请重新发起登录。");
                }

                var payload = JsonSerializer.Deserialize<DeviceSessionPayload>(
                    protector.Decrypt(row.SecretCipher),
                    JsonOptions) ?? throw new ApiException(500, "server.error", "设备授权状态损坏，请重新发起登录。");
                return new DevicePollResult(
                    "approved",
                    new SessionTokens(
                        payload.AccessToken,
                        payload.RefreshToken,
                        payload.AccessExpiresAt,
                        payload.AccessExpiresIn,
                        payload.RefreshExpiresAt,
                        payload.User),
                    Two);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveAsync(SessionUser user, string? rawUserCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var compact = Tokens.NormalizeUserCode(rawUserCode)
            ?? throw new ApiException(404, "device.invalid_code", "授权码不存在或已过期。");
        if (!limiter.TryAcquire("device-approve:" + user.Id, 10, TimeSpan.FromMinutes(1)))
        {
            throw new ApiException(429, "device.rate_limited", "请求过于频繁，请稍后再试。", retryAfterSeconds: 60);
        }

        var userCodeHash = hasher.UserCodeHash(compact);
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Now();
                var row = await connection.SingleAsync(
                    transaction,
                    """
                    SELECT id, state, expires_at, device_label, platform
                    FROM device_auth WHERE user_code_hash = $hash
                    """,
                    reader => new ApprovalRow(
                        reader.Str("id"),
                        reader.Str("state"),
                        reader.Long("expires_at"),
                        reader.Str("device_label"),
                        reader.Str("platform")),
                    token,
                    ("$hash", userCodeHash)).ConfigureAwait(false);

                if (row is null || row.ExpiresAt <= now)
                {
                    throw new ApiException(404, "device.invalid_code", "授权码不存在或已过期。");
                }

                if (row.State is not "pending")
                {
                    throw new ApiException(409, "device.already_handled", "该授权码已被使用。");
                }

                var updated = await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE device_auth SET state = 'approved', user_id = $user, approved_at = $now
                    WHERE id = $id AND state = 'pending'
                    """,
                    token,
                    ("$user", user.Id),
                    ("$now", now),
                    ("$id", row.Id)).ConfigureAwait(false);
                if (updated != 1)
                {
                    throw new ApiException(409, "device.already_handled", "该授权码已被使用。");
                }

                var tokens = await sessions.CreateWithinTransactionAsync(
                    connection,
                    transaction,
                    user,
                    row.DeviceLabel,
                    row.Platform,
                    token).ConfigureAwait(false);
                var cipher = protector.Encrypt(JsonSerializer.Serialize(
                    new DeviceSessionPayload(
                        tokens.AccessToken,
                        tokens.RefreshToken,
                        tokens.AccessExpiresAt,
                        tokens.AccessExpiresIn,
                        tokens.RefreshExpiresAt,
                        tokens.User),
                    JsonOptions));
                await connection.ExecuteAsync(
                    transaction,
                    "UPDATE device_auth SET secret_cipher = $cipher WHERE id = $id",
                    token,
                    ("$cipher", cipher),
                    ("$id", row.Id)).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DenyAsync(SessionUser user, string? rawUserCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var compact = Tokens.NormalizeUserCode(rawUserCode)
            ?? throw new ApiException(404, "device.invalid_code", "授权码不存在或已过期。");
        var userCodeHash = hasher.UserCodeHash(compact);
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var updated = await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE device_auth SET state = 'denied', user_id = $user
                    WHERE user_code_hash = $hash AND state = 'pending' AND expires_at > $now
                    """,
                    token,
                    ("$user", user.Id),
                    ("$hash", userCodeHash),
                    ("$now", Now())).ConfigureAwait(false);
                if (updated != 1)
                {
                    throw new ApiException(404, "device.invalid_code", "授权码不存在或已过期。");
                }

                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private const int Two = 2;

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private sealed record DeviceRow(string Id, string State, long ExpiresAt, string? SecretCipher, long? LastPolledAt);

    private sealed record ApprovalRow(string Id, string State, long ExpiresAt, string DeviceLabel, string Platform);

    private sealed record InfoRow(
        string? UserId,
        string State,
        string DeviceLabel,
        string Platform,
        string AppVersion,
        long ExpiresAt,
        long? ApprovedAt);

    private sealed record DeviceSessionPayload(
        string AccessToken,
        string RefreshToken,
        long AccessExpiresAt,
        long AccessExpiresIn,
        long RefreshExpiresAt,
        SessionUser User);
}
