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
    long RefreshExpiresAt,
    SessionUser User);

public sealed record UserCredentials(SessionUser User, byte[] Salt, byte[] Hash);

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
    public async Task<SessionUser?> AuthenticateAsync(string? accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var hash = Tokens.Hash(accessToken);
        var now = Now();
        return await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                """
                SELECT u.id, u.email, u.created_at, u.email_verified_at
                FROM sessions s JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = $hash AND s.revoked_at IS NULL AND s.expires_at > $now
                """,
                MapUser,
                token,
                ("$hash", hash),
                ("$now", now)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        return await database.ReadAsync(
            async (connection, token) => await connection.SingleAsync(
                transaction: null,
                "SELECT id, email, created_at, email_verified_at FROM users WHERE email = $email",
                MapUser,
                token,
                ("$email", email)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

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
        CancellationToken cancellationToken)
    {
        var familyId = Guid.NewGuid().ToString("N");
        return await database.WriteAsync(
            async (connection, transaction, token) =>
                (await InsertAsync(connection, transaction, user, familyId, deviceLabel, token)
                    .ConfigureAwait(false)).Tokens,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Used by the device flow so approval is one atomic transaction.</summary>
    internal async Task<SessionTokens> CreateWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionUser user,
        string deviceLabel,
        CancellationToken cancellationToken)
    {
        var familyId = Guid.NewGuid().ToString("N");
        var created = await InsertAsync(connection, transaction, user, familyId, deviceLabel, cancellationToken)
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
                    SELECT id, user_id, family_id, expires_at, revoked_at, device_label
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

                var created = await InsertAsync(connection, transaction, user, row.FamilyId, row.DeviceLabel, token)
                    .ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var now = Now();
        var accessExpires = now + options.AccessTokenSeconds;
        var refreshExpires = now + options.RefreshTokenSeconds;
        var accessToken = Tokens.Create();
        var refreshToken = Tokens.Create();
        var refreshId = Guid.NewGuid().ToString("N");
        var label = deviceLabel.Length > 120 ? deviceLabel[..120] : deviceLabel;

        await connection.ExecuteAsync(
            transaction,
            """
            INSERT INTO sessions(id, user_id, token_hash, family_id, created_at, expires_at, device_label)
            VALUES($id, $user, $hash, $family, $now, $expires, $label)
            """,
            cancellationToken,
            ("$id", Guid.NewGuid().ToString("N")),
            ("$user", user.Id),
            ("$hash", Tokens.Hash(accessToken)),
            ("$family", familyId),
            ("$now", now),
            ("$expires", accessExpires),
            ("$label", label)).ConfigureAwait(false);

        await connection.ExecuteAsync(
            transaction,
            """
            INSERT INTO refresh_tokens(id, user_id, token_hash, family_id, created_at, expires_at, device_label)
            VALUES($id, $user, $hash, $family, $now, $expires, $label)
            """,
            cancellationToken,
            ("$id", refreshId),
            ("$user", user.Id),
            ("$hash", Tokens.Hash(refreshToken)),
            ("$family", familyId),
            ("$now", now),
            ("$expires", refreshExpires),
            ("$label", label)).ConfigureAwait(false);

        return (new SessionTokens(accessToken, refreshToken, accessExpires, refreshExpires, user), refreshId);
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
            reader.LongOrNull("revoked_at"), reader.Str("device_label"));

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private sealed record RefreshRow(
        string Id,
        string UserId,
        string FamilyId,
        long ExpiresAt,
        long? RevokedAt,
        string DeviceLabel);

    private sealed record FamilyRow(string Id, string UserId, string FamilyId);

    private sealed record RefreshOutcome(SessionTokens? Tokens, int Status, string Code, string Message)
    {
        public static RefreshOutcome Failed(int status, string code, string message) =>
            new(null, status, code, message);
    }
}
