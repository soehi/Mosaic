using Microsoft.Data.Sqlite;
using Mosaic.Server.Data;
using Mosaic.Server.Http;
using Mosaic.Server.RateLimiting;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

internal sealed class MobilePairingService(
    Database database, SessionService sessions, SecretHasher hasher, WindowLimiter limiter, TimeProvider clock)
{
    private const int LifetimeSeconds = 300;
    private const int IntervalSeconds = 2;

    public Task<string> StartAsync(SessionPrincipal principal, CancellationToken ct)
    {
        Limit("start:" + principal.FamilyId, 10);
        var reference = Tokens.Create();
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var now = Now();
            await RequireActiveAsync(connection, transaction, principal.User.Id, principal.FamilyId, now, token);
            await connection.ExecuteAsync(transaction,
                """
                DELETE FROM device_auth WHERE id IN (SELECT device_id FROM mobile_pairings WHERE expires_at <= $now);
                DELETE FROM mobile_pairings WHERE expires_at <= $now;
                UPDATE mobile_pairings SET cancelled_at = $now
                WHERE user_id = $user AND family_id = $family AND cancelled_at IS NULL
                    AND COALESCE((SELECT state FROM device_auth WHERE id = device_id), 'pending') <> 'consumed';
                INSERT INTO mobile_pairings(reference_hash, user_id, family_id, expires_at)
                VALUES($reference, $user, $family, $expires);
                """, token, ("$reference", Tokens.Hash(reference)), ("$user", principal.User.Id),
                ("$family", principal.FamilyId), ("$now", now), ("$expires", now + LifetimeSeconds));
            return reference;
        }, ct);
    }

    public Task<MobileClaim> ClaimAsync(string? reference, string? label, string? version, string ipHash, CancellationToken ct)
    {
        Limit("claim:" + ipHash, 30);
        var hash = Hash(reference);
        var deviceCode = Tokens.Create();
        var shortCode = Tokens.CreateEmailCode();
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var row = await FindAsync(connection, transaction, "p.reference_hash", hash, token) ?? throw Invalid();
            var now = Now();
            if (!row.Active(now) || row.State != "waiting")
            {
                throw Invalid();
            }

            await RequireActiveAsync(connection, transaction, row.User.Id, row.FamilyId, now, token);
            var id = Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO device_auth(id, user_code_hash, device_code_hash, state, user_id,
                    device_label, platform, app_version, created_at, expires_at)
                VALUES($id, $unused, $code, 'pending', $user, $label, 'android', $version, $now, $expires);
                UPDATE mobile_pairings SET device_id = $id, short_code = $short WHERE reference_hash = $ref;
                """, token, ("$id", id), ("$unused", Tokens.Hash(Tokens.Create())),
                ("$code", hasher.DeviceCodeHash(deviceCode)), ("$user", row.User.Id),
                ("$label", AuthSupport.SanitizeDeviceLabel(label)), ("$version", AuthSupport.SanitizeShort(version)),
                ("$now", now), ("$expires", row.ExpiresAt), ("$short", shortCode), ("$ref", hash));
            return new MobileClaim(deviceCode, shortCode, row.ExpiresAt - now, IntervalSeconds);
        }, ct);
    }

    public Task<MobilePairingStatus> StatusAsync(SessionPrincipal principal, string? reference, CancellationToken ct)
    {
        var hash = Hash(reference);
        return database.ReadAsync(async (connection, token) =>
        {
            var row = Owned(await FindAsync(connection, null, "p.reference_hash", hash, token), principal);
            return new MobilePairingStatus(row.Cancelled ? "cancelled" : row.ExpiresAt <= Now() ? "expired" : row.State,
                row.DeviceLabel, row.AppVersion);
        }, ct);
    }

    public Task ApproveAsync(SessionPrincipal principal, string? reference, string? shortCode, CancellationToken ct)
    {
        Limit("approve:" + principal.FamilyId, 15);
        var hash = Hash(reference);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var row = Owned(await FindAsync(connection, transaction, "p.reference_hash", hash, token), principal);
            var now = Now();
            if (!row.Active(now) || row.State != "pending" || row.ShortCode != shortCode)
            {
                throw new ApiException(409, "mobile.approval_invalid", "配对状态已变更或配对码不匹配。");
            }

            await RequireActiveAsync(connection, transaction, principal.User.Id, principal.FamilyId, now, token);
            return await connection.ExecuteAsync(transaction,
                "UPDATE device_auth SET state = 'approved', approved_at = $now WHERE id = $id",
                token, ("$now", now), ("$id", row.DeviceId));
        }, ct);
    }

    public Task CancelAsync(SessionPrincipal principal, string? reference, CancellationToken ct)
    {
        var hash = Hash(reference);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var row = Owned(await FindAsync(connection, transaction, "p.reference_hash", hash, token), principal);
            if (row.State != "consumed")
            {
                await connection.ExecuteAsync(transaction,
                    "UPDATE mobile_pairings SET cancelled_at = $now WHERE reference_hash = $ref AND cancelled_at IS NULL",
                    token, ("$now", Now()), ("$ref", hash));
            }

            return 0;
        }, ct);
    }

    public Task<DevicePollResult> PollAsync(string? deviceCode, string ipHash, CancellationToken ct)
    {
        Hash(deviceCode);
        var hash = hasher.DeviceCodeHash(deviceCode!);
        Limit("poll-ip:" + ipHash, 180);
        Limit("poll:" + hash, 60);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var row = await FindAsync(connection, transaction, "d.device_code_hash", hash, token) ?? throw Invalid();
            var now = Now();
            if (row.State == "consumed")
            {
                return new DevicePollResult("consumed", null);
            }

            if (!row.Active(now) || !await IsActiveAsync(connection, transaction, row.User.Id, row.FamilyId, now, token))
            {
                return new DevicePollResult(row.ExpiresAt <= now ? "expired" : "denied", null);
            }

            if (row.State == "pending")
            {
                if (row.LastPolledAt is { } last && now - last < IntervalSeconds)
                {
                    return new DevicePollResult("slow_down", null);
                }

                await connection.ExecuteAsync(transaction,
                    "UPDATE device_auth SET last_polled_at = $now WHERE id = $id", token,
                    ("$now", now), ("$id", row.DeviceId));
                return new DevicePollResult("pending", null);
            }

            var session = await sessions.CreateWithinTransactionAsync(connection, transaction, row.User,
                row.DeviceLabel ?? string.Empty, "android", token);
            await connection.ExecuteAsync(transaction,
                "UPDATE device_auth SET state = 'consumed', consumed_at = $now WHERE id = $id",
                token, ("$now", now), ("$id", row.DeviceId));
            return new DevicePollResult("approved", session);
        }, ct);
    }

    private static Task<Pairing?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string column, string hash, CancellationToken ct) => connection.SingleAsync(transaction,
        $"""
        SELECT p.family_id, p.expires_at, p.cancelled_at, p.short_code, d.id AS device_id,
            COALESCE(d.state, 'waiting') AS state, d.device_label, d.app_version, d.last_polled_at,
            u.id AS user_id, u.email, u.created_at, u.email_verified_at
        FROM mobile_pairings p JOIN users u ON u.id = p.user_id
        LEFT JOIN device_auth d ON d.id = p.device_id WHERE {column} = $hash
        """,
        reader => new Pairing(
            new SessionUser(reader.Str("user_id"), reader.Str("email"), reader.Long("created_at"), reader.Long("email_verified_at")),
            reader.Str("family_id"), reader.Long("expires_at"), reader.LongOrNull("cancelled_at") is not null,
            reader.StrOrNull("short_code"), reader.StrOrNull("device_id"), reader.Str("state"),
            reader.StrOrNull("device_label"), reader.StrOrNull("app_version"), reader.LongOrNull("last_polled_at")),
        ct, ("$hash", hash));

    private static async Task<bool> IsActiveAsync(SqliteConnection connection, SqliteTransaction transaction,
        string userId, string familyId, long now, CancellationToken ct) =>
        await connection.SingleAsync(transaction,
            """
            SELECT id FROM refresh_tokens WHERE user_id = $user AND family_id = $family
                AND revoked_at IS NULL AND expires_at > $now LIMIT 1
            """, reader => reader.Str("id"), ct, ("$user", userId), ("$family", familyId), ("$now", now)) is not null;

    private static async Task RequireActiveAsync(SqliteConnection connection, SqliteTransaction transaction,
        string userId, string familyId, long now, CancellationToken ct)
    {
        if (!await IsActiveAsync(connection, transaction, userId, familyId, now, ct))
        {
            throw Invalid();
        }
    }

    private static Pairing Owned(Pairing? row, SessionPrincipal principal) =>
        row is not null && row.User.Id == principal.User.Id && row.FamilyId == principal.FamilyId ? row : throw Invalid();

    private static string Hash(string? reference) =>
        reference is { Length: 43 } && reference.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? Tokens.Hash(reference) : throw Invalid();

    private void Limit(string key, int count)
    {
        if (!limiter.TryAcquire("mobile:" + key, count, TimeSpan.FromMinutes(1)))
        {
            throw new ApiException(429, "mobile.rate_limited", "请求过于频繁，请稍后再试。", retryAfterSeconds: 60);
        }
    }

    private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

    private static ApiException Invalid() => new(404, "mobile.invalid_pairing", "配对已失效，请从桌面重新扫码。");

    private sealed record Pairing(SessionUser User, string FamilyId, long ExpiresAt, bool Cancelled,
        string? ShortCode, string? DeviceId, string State, string? DeviceLabel, string? AppVersion, long? LastPolledAt)
    {
        public bool Active(long now) => !Cancelled && now < ExpiresAt;
    }
}

internal sealed record MobileClaim(string DeviceCode, string ShortCode, long ExpiresIn, int Interval);
internal sealed record MobilePairingStatus(string State, string? DeviceLabel, string? AppVersion);
