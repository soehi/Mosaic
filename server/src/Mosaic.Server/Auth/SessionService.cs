using Microsoft.Data.Sqlite;
using Mosaic.Server.Data;
using Mosaic.Server.Http;
using Mosaic.Server.Security;

namespace Mosaic.Server.Auth;

public sealed record SessionUser(string Id, string Email, long CreatedAt, long EmailVerifiedAt);

public sealed record SessionTokens(
    string AccessToken,
    string RefreshToken,
    long AccessExpiresAt,
    long AccessExpiresIn,
    long RefreshExpiresAt,
    SessionUser User);

public sealed record UserCredentials(SessionUser User, byte[] Salt, byte[] Hash);

public sealed record SessionPrincipal(SessionUser User, string SessionId, string FamilyId);

public sealed record SessionInfo(
    string Id,
    string DeviceLabel,
    string Platform,
    long CreatedAt,
    long LastSeenAt,
    long ExpiresAt,
    bool Current);

/// <summary>
/// Opaque access tokens (15 minutes) and rotating refresh tokens (14 days).
/// Only SHA-256 hashes are stored; a refresh token that is presented after it
/// was rotated revokes the whole family.
/// </summary>
public sealed class SessionService(
    Database database,
    ServerOptions options,
    SecretHasher hasher,
    TimeProvider timeProvider)
{
    private const long LastSeenWriteIntervalSeconds = 60;

    public async Task<SessionPrincipal> AuthenticateAsync(string? accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ApiException(401, "auth.required", "请先登录。");
        }

        var hash = Tokens.Hash(accessToken);
        var now = Now();
        var row = await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                """
                SELECT s.id AS session_id, s.family_id, s.revoked_at, s.expires_at, s.last_seen_at,
                       u.id, u.email, u.created_at, u.email_verified_at
                FROM sessions s JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = $hash
                """,
                reader => new SessionRow(
                    reader.Str("session_id"),
                    reader.Str("family_id"),
                    reader.LongOrNull("revoked_at"),
                    reader.Long("expires_at"),
                    reader.Long("last_seen_at"),
                    MapUser(reader)),
                token,
                ("$hash", hash)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            throw new ApiException(401, "auth.required", "请先登录。");
        }

        if (row.RevokedAt is not null)
        {
            throw new ApiException(401, "auth.session_revoked", "登录状态已失效，请重新登录。");
        }

        if (row.ExpiresAt <= now)
        {
            throw new ApiException(401, "auth.session_expired", "登录已过期，请重新登录。");
        }

        if (now - row.LastSeenAt >= LastSeenWriteIntervalSeconds)
        {
            await TouchAsync(row.SessionId, row.FamilyId, now, cancellationToken).ConfigureAwait(false);
        }

        return new SessionPrincipal(row.User, row.SessionId, row.FamilyId);
    }

    public async Task<SessionUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                "SELECT id, email, created_at, email_verified_at FROM users WHERE email = $email",
                MapUser,
                token,
                ("$email", email)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async Task<UserCredentials?> FindCredentialsAsync(string email, CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                """
                SELECT id, email, created_at, email_verified_at, password_salt, password_hash
                FROM users WHERE email = $email
                """,
                reader => new UserCredentials(
                    MapUser(reader),
                    reader.Blob("password_salt"),
                    reader.Blob("password_hash")),
                token,
                ("$email", email)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async Task<SessionTokens> CreateAsync(
        SessionUser user,
        string deviceLabel,
        string platform,
        CancellationToken cancellationToken)
    {
        var familyId = Guid.NewGuid().ToString("N");
        return await database.WriteAsync(
            async (connection, transaction, token) =>
                (await InsertAsync(connection, transaction, user, familyId, deviceLabel, platform, token)
                    .ConfigureAwait(false)).Tokens,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Used by the device flow so approval is one atomic transaction.</summary>
    internal async Task<SessionTokens> CreateWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionUser user,
        string deviceLabel,
        string platform,
        CancellationToken cancellationToken)
    {
        var familyId = Guid.NewGuid().ToString("N");
        var created = await InsertAsync(connection, transaction, user, familyId, deviceLabel, platform, cancellationToken)
            .ConfigureAwait(false);
        return created.Tokens;
    }

    public async Task<SessionTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = Tokens.Hash(refreshToken);
        // Reuse detection must persist the family revocation, so failures are returned
        // from the transaction and only thrown after it commits.
        var outcome = await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Now();
                var row = await connection.SingleAsync(
                    transaction,
                    """
                    SELECT id, user_id, family_id, expires_at, revoked_at, device_label, platform
                    FROM refresh_tokens WHERE token_hash = $hash
                    """,
                    MapRefresh,
                    token,
                    ("$hash", hash)).ConfigureAwait(false);

                if (row is null)
                {
                    return RefreshOutcome.Failed(401, "auth.invalid_refresh", "登录已过期，请重新登录。");
                }

                if (row.RevokedAt is not null)
                {
                    await RevokeFamilyAsync(connection, transaction, row.FamilyId, now, token)
                        .ConfigureAwait(false);
                    return RefreshOutcome.Failed(401, "auth.refresh_reused", "登录状态已失效，请重新登录。");
                }

                if (row.ExpiresAt <= now)
                {
                    return RefreshOutcome.Failed(401, "auth.session_expired", "登录已过期，请重新登录。");
                }

                var user = await LoadUserAsync(connection, transaction, row.UserId, token).ConfigureAwait(false);
                if (user is null)
                {
                    return RefreshOutcome.Failed(401, "auth.session_expired", "登录已过期，请重新登录。");
                }

                var created = await InsertAsync(
                    connection,
                    transaction,
                    user,
                    row.FamilyId,
                    row.DeviceLabel,
                    row.Platform,
                    token).ConfigureAwait(false);
                var updated = await connection.ExecuteAsync(
                    transaction,
                    "UPDATE refresh_tokens SET revoked_at = $now, replaced_by = $next WHERE id = $id AND revoked_at IS NULL",
                    token,
                    ("$now", now),
                    ("$next", created.RefreshId),
                    ("$id", row.Id)).ConfigureAwait(false);
                if (updated != 1)
                {
                    await RevokeFamilyAsync(connection, transaction, row.FamilyId, now, token)
                        .ConfigureAwait(false);
                    return RefreshOutcome.Failed(401, "auth.refresh_reused", "登录状态已失效，请重新登录。");
                }

                return new RefreshOutcome(created.Tokens, 0, string.Empty, string.Empty);
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Tokens is null)
        {
            throw new ApiException(outcome.Status, outcome.Code, outcome.Message);
        }

        return outcome.Tokens;
    }

    public async Task LogoutAsync(
        string userId,
        string refreshToken,
        bool allDevices,
        CancellationToken cancellationToken)
    {
        var hash = Tokens.Hash(refreshToken);
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Now();
                if (allDevices)
                {
                    await RevokeUserTokensAsync(connection, transaction, userId, now, token).ConfigureAwait(false);
                    return 0;
                }

                var row = await connection.SingleAsync(
                    transaction,
                    "SELECT id, user_id, family_id FROM refresh_tokens WHERE token_hash = $hash",
                    reader => new FamilyRow(reader.Str("id"), reader.Str("user_id"), reader.Str("family_id")),
                    token,
                    ("$hash", hash)).ConfigureAwait(false);
                if (row is null || !string.Equals(row.UserId, userId, StringComparison.Ordinal))
                {
                    return 0;
                }

                await RevokeFamilyAsync(connection, transaction, row.FamilyId, now, token).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAllAsync(string userId, string email, CancellationToken cancellationToken)
    {
        var emailHash = hasher.EmailHash(email);
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Now();
                await RevokeUserTokensAsync(connection, transaction, userId, now, token).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    "UPDATE email_codes SET consumed_at = $now WHERE email_hash = $hash AND consumed_at IS NULL",
                    token,
                    ("$now", now),
                    ("$hash", emailHash)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE device_auth SET state = 'denied', secret_cipher = NULL
                    WHERE user_id = $user AND state IN ('pending', 'approved')
                    """,
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<SessionInfo>> ListSessionsAsync(
        string userId,
        string currentFamilyId,
        CancellationToken cancellationToken)
    {
        var now = Now();
        return await database.ReadAsync(
            async (connection, token) => await connection.QueryAsync(
                transaction: null,
                """
                SELECT family_id AS id,
                       MAX(device_label) AS device_label,
                       MAX(platform) AS platform,
                       MIN(created_at) AS created_at,
                       MAX(last_seen_at) AS last_seen_at,
                       MAX(expires_at) AS expires_at
                FROM refresh_tokens
                WHERE user_id = $user AND revoked_at IS NULL AND expires_at > $now
                GROUP BY family_id
                ORDER BY MAX(last_seen_at) DESC, MIN(created_at) DESC
                """,
                reader => new SessionInfo(
                    reader.Str("id"),
                    reader.Str("device_label"),
                    reader.Str("platform"),
                    reader.Long("created_at"),
                    reader.Long("last_seen_at"),
                    reader.Long("expires_at"),
                    string.Equals(reader.Str("id"), currentFamilyId, StringComparison.Ordinal)),
                token,
                ("$user", userId),
                ("$now", now)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeSessionAsync(
        string userId,
        string familyId,
        CancellationToken cancellationToken)
    {
        var now = Now();
        return await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var exists = await connection.ScalarInt64Async(
                    transaction,
                    "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = $user AND family_id = $family",
                    token,
                    ("$user", userId),
                    ("$family", familyId)).ConfigureAwait(false);
                if (exists == 0)
                {
                    return false;
                }

                await RevokeFamilyAsync(connection, transaction, familyId, now, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAllSessionsAsync(
        string userId,
        string? keepFamilyId,
        CancellationToken cancellationToken)
    {
        var now = Now();
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                if (keepFamilyId is null)
                {
                    await RevokeUserTokensAsync(connection, transaction, userId, now, token).ConfigureAwait(false);
                    return 0;
                }

                await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE refresh_tokens SET revoked_at = $now
                    WHERE user_id = $user AND revoked_at IS NULL AND family_id <> $keep
                    """,
                    token,
                    ("$now", now),
                    ("$user", userId),
                    ("$keep", keepFamilyId)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE sessions SET revoked_at = $now
                    WHERE user_id = $user AND revoked_at IS NULL AND family_id <> $keep
                    """,
                    token,
                    ("$now", now),
                    ("$user", userId),
                    ("$keep", keepFamilyId)).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the account and every row owned by it. Foreign keys cascade to
    /// sessions, refresh tokens, device authorizations, backups, sync items,
    /// changes, counters, cursors and the sync floor. Mail codes are keyed by
    /// address hash and are removed explicitly. Audit rows are re-identified with
    /// a truncated pseudonym so they survive without the account id.
    /// </summary>
    public async Task DeleteAccountAsync(string userId, string email, CancellationToken cancellationToken)
    {
        var emailHash = hasher.EmailHash(email);
        var anonymousId = "deleted:" + emailHash[..16];
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Now();
                await connection.ExecuteAsync(
                    transaction,
                    "UPDATE settings_audit SET user_id = $anonymous WHERE user_id = $user",
                    token,
                    ("$anonymous", anonymousId),
                    ("$user", userId)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    "INSERT INTO settings_audit(user_id, action, created_at) VALUES($anonymous, 'account.delete', $now)",
                    token,
                    ("$anonymous", anonymousId),
                    ("$now", now)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    "DELETE FROM email_codes WHERE email_hash = $hash",
                    token,
                    ("$hash", emailHash)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    "DELETE FROM users WHERE id = $user",
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionUser> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var (salt, hash) = PasswordHasher.Hash(password);
        var id = Guid.NewGuid().ToString("N");
        var now = Now();
        try
        {
            return await database.WriteAsync(
                async (connection, transaction, token) =>
                {
                    var exists = await connection.ScalarInt64Async(
                        transaction,
                        "SELECT COUNT(*) FROM users WHERE email = $email",
                        token,
                        ("$email", email)).ConfigureAwait(false);
                    if (exists > 0)
                    {
                        throw new ApiException(400, "auth.invalid_code", "验证码无效或已过期。");
                    }

                    await connection.ExecuteAsync(
                        transaction,
                        """
                        INSERT INTO users(id, email, password_salt, password_hash, created_at, email_verified_at)
                        VALUES($id, $email, $salt, $hash, $now, $now)
                        """,
                        token,
                        ("$id", id),
                        ("$email", email),
                        ("$salt", salt),
                        ("$hash", hash),
                        ("$now", now)).ConfigureAwait(false);
                    return new SessionUser(id, email, now, now);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ApiException(400, "auth.invalid_code", "验证码无效或已过期。");
        }
    }

    public async Task UpdatePasswordAsync(string userId, string password, CancellationToken cancellationToken)
    {
        var (salt, hash) = PasswordHasher.Hash(password);
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                await connection.ExecuteAsync(
                    transaction,
                    "UPDATE users SET password_salt = $salt, password_hash = $hash WHERE id = $id",
                    token,
                    ("$salt", salt),
                    ("$hash", hash),
                    ("$id", userId)).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionUser?> FindUserByIdAsync(string userId, CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                "SELECT id, email, created_at, email_verified_at FROM users WHERE id = $id",
                MapUser,
                token,
                ("$id", userId)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private async Task<(SessionTokens Tokens, string RefreshId)> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionUser user,
        string familyId,
        string deviceLabel,
        string platform,
        CancellationToken cancellationToken)
    {
        var now = Now();
        var accessExpires = now + options.AccessTokenSeconds;
        var refreshExpires = now + options.RefreshTokenSeconds;
        var accessToken = Tokens.Create();
        var refreshToken = Tokens.Create();
        var refreshId = Guid.NewGuid().ToString("N");
        var label = deviceLabel.Length > 120 ? deviceLabel[..120] : deviceLabel;
        var platformName = platform.Length > 40 ? platform[..40] : platform;

        await connection.ExecuteAsync(
            transaction,
            """
            INSERT INTO sessions(
                id, user_id, token_hash, family_id, created_at, expires_at, device_label, platform, last_seen_at)
            VALUES($id, $user, $hash, $family, $now, $expires, $label, $platform, $now)
            """,
            cancellationToken,
            ("$id", Guid.NewGuid().ToString("N")),
            ("$user", user.Id),
            ("$hash", Tokens.Hash(accessToken)),
            ("$family", familyId),
            ("$now", now),
            ("$expires", accessExpires),
            ("$label", label),
            ("$platform", platformName)).ConfigureAwait(false);

        await connection.ExecuteAsync(
            transaction,
            """
            INSERT INTO refresh_tokens(
                id, user_id, token_hash, family_id, created_at, expires_at, device_label, platform, last_seen_at)
            VALUES($id, $user, $hash, $family, $now, $expires, $label, $platform, $now)
            """,
            cancellationToken,
            ("$id", refreshId),
            ("$user", user.Id),
            ("$hash", Tokens.Hash(refreshToken)),
            ("$family", familyId),
            ("$now", now),
            ("$expires", refreshExpires),
            ("$label", label),
            ("$platform", platformName)).ConfigureAwait(false);

        return (
            new SessionTokens(
                accessToken,
                refreshToken,
                accessExpires,
                options.AccessTokenSeconds,
                refreshExpires,
                user),
            refreshId);
    }

    private async Task TouchAsync(
        string sessionId,
        string familyId,
        long now,
        CancellationToken cancellationToken)
    {
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var threshold = now - LastSeenWriteIntervalSeconds;
                await connection.ExecuteAsync(
                    transaction,
                    "UPDATE sessions SET last_seen_at = $now WHERE id = $id AND last_seen_at < $threshold",
                    token,
                    ("$now", now),
                    ("$id", sessionId),
                    ("$threshold", threshold)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    """
                    UPDATE refresh_tokens SET last_seen_at = $now
                    WHERE family_id = $family AND last_seen_at < $threshold
                    """,
                    token,
                    ("$now", now),
                    ("$family", familyId),
                    ("$threshold", threshold)).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RevokeFamilyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string familyId,
        long now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            transaction,
            "UPDATE refresh_tokens SET revoked_at = $now WHERE family_id = $family AND revoked_at IS NULL",
            cancellationToken,
            ("$now", now),
            ("$family", familyId)).ConfigureAwait(false);
        await connection.ExecuteAsync(
            transaction,
            "UPDATE sessions SET revoked_at = $now WHERE family_id = $family AND revoked_at IS NULL",
            cancellationToken,
            ("$now", now),
            ("$family", familyId)).ConfigureAwait(false);
    }

    private static async Task RevokeUserTokensAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        long now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            transaction,
            "UPDATE sessions SET revoked_at = $now WHERE user_id = $user AND revoked_at IS NULL",
            cancellationToken,
            ("$now", now),
            ("$user", userId)).ConfigureAwait(false);
        await connection.ExecuteAsync(
            transaction,
            "UPDATE refresh_tokens SET revoked_at = $now WHERE user_id = $user AND revoked_at IS NULL",
            cancellationToken,
            ("$now", now),
            ("$user", userId)).ConfigureAwait(false);
    }

    private static async Task<SessionUser?> LoadUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken) =>
        await connection.SingleAsync(
            transaction,
            "SELECT id, email, created_at, email_verified_at FROM users WHERE id = $id",
            MapUser,
            cancellationToken,
            ("$id", userId)).ConfigureAwait(false);

    internal static SessionUser MapUser(SqliteDataReader reader) =>
        new(reader.Str("id"), reader.Str("email"), reader.Long("created_at"), reader.Long("email_verified_at"));

    private static RefreshRow MapRefresh(SqliteDataReader reader) =>
        new(reader.Str("id"), reader.Str("user_id"), reader.Str("family_id"), reader.Long("expires_at"),
            reader.LongOrNull("revoked_at"), reader.Str("device_label"), reader.Str("platform"));

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private sealed record SessionRow(
        string SessionId,
        string FamilyId,
        long? RevokedAt,
        long ExpiresAt,
        long LastSeenAt,
        SessionUser User);

    private sealed record RefreshRow(
        string Id,
        string UserId,
        string FamilyId,
        long ExpiresAt,
        long? RevokedAt,
        string DeviceLabel,
        string Platform);

    private sealed record FamilyRow(string Id, string UserId, string FamilyId);

    private sealed record RefreshOutcome(SessionTokens? Tokens, int Status, string Code, string Message)
    {
        public static RefreshOutcome Failed(int status, string code, string message) =>
            new(null, status, code, message);
    }
}
