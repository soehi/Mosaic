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
    long ExpiresIn,
    int Interval);

public sealed record DevicePollResult(string Status, SessionTokens? Session);

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
                                created_at, expires_at)
                            VALUES($id, $user, $device, 'pending', $label, $now, $expires)
                            """,
                            token,
                            ("$id", Guid.NewGuid().ToString("N")),
                            ("$user", hasher.UserCodeHash(compact)),
                            ("$device", hasher.DeviceCodeHash(deviceCode)),
                            ("$label", label),
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

            var verificationUri = options.PublicUrl + "/device?code=" + Uri.EscapeDataString(userCode);
            return new DeviceStartResult(
                deviceCode,
                userCode,
                verificationUri,
                options.DeviceCodeTtlSeconds,
                2);
        }

        throw new ApiException(503, "server.error", "暂时无法创建授权码，请稍后重试。");
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
                    SELECT id, state, expires_at, secret_cipher
                    FROM device_auth WHERE device_code_hash = $hash
                    """,
                    reader => new DeviceRow(
                        reader.Str("id"),
                        reader.Str("state"),
                        reader.Long("expires_at"),
                        reader.StrOrNull("secret_cipher")),
                    token,
                    ("$hash", deviceCodeHash)).ConfigureAwait(false);

                if (row is null)
                {
                    throw new ApiException(400, "device.invalid_code", "授权码无效。");
                }

                if (row.State is "consumed")
                {
                    return new DevicePollResult("consumed", null);
                }

                if (row.State is "denied")
                {
                    return new DevicePollResult("denied", null);
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

                    return new DevicePollResult("expired", null);
                }

                if (row.State is "pending")
                {
                    return new DevicePollResult("pending", null);
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
                    return new DevicePollResult("consumed", null);
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
                        payload.RefreshExpiresAt,
                        payload.User));
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
                    SELECT id, state, expires_at, device_label
                    FROM device_auth WHERE user_code_hash = $hash
                    """,
                    reader => new ApprovalRow(
                        reader.Str("id"),
                        reader.Str("state"),
                        reader.Long("expires_at"),
                        reader.Str("device_label")),
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
                    token).ConfigureAwait(false);
                var cipher = protector.Encrypt(JsonSerializer.Serialize(
                    new DeviceSessionPayload(
                        tokens.AccessToken,
                        tokens.RefreshToken,
                        tokens.AccessExpiresAt,
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

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private sealed record DeviceRow(string Id, string State, long ExpiresAt, string? SecretCipher);

    private sealed record ApprovalRow(string Id, string State, long ExpiresAt, string DeviceLabel);

    private sealed record DeviceSessionPayload(
        string AccessToken,
        string RefreshToken,
        long AccessExpiresAt,
        long RefreshExpiresAt,
        SessionUser User);
}
