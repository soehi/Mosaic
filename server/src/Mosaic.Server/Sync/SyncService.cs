using System.Globalization;
using System.Text;
using System.Text.Json;
using Mosaic.Server.Backups;
using Mosaic.Server.Data;
using Mosaic.Server.Http;

namespace Mosaic.Server.Sync;

public sealed record SyncChangeRequest(
    string? DeviceId,
    string? Kind,
    string? Key,
    JsonElement Payload,
    bool? Deleted,
    long? BaseServerRevision,
    long? ClientRevision,
    string? IdempotencyKey);

public sealed record SyncChange(
    long Seq,
    long ServerRevision,
    string Kind,
    string Key,
    bool Deleted,
    JsonElement? Payload,
    string DeviceId,
    long CreatedAt);

public sealed record SyncPushResult(SyncChange Change, bool Replayed);

public sealed record SyncPullResult(IReadOnlyList<SyncChange> Changes, long NextCursor, bool HasMore);

/// <summary>
/// Incremental sync. The server owns revisions: clients send the revision they
/// last saw (baseServerRevision) and an idempotency key; stale bases get a 409
/// carrying the current server state instead of silently overwriting it.
/// </summary>
public sealed class SyncService(Database database, ServerOptions options, TimeProvider timeProvider)
{
    public const long MaxPayloadBytes = 1024 * 1024;

    private const string KindTask = "task";

    private const string KindSetting = "setting";

    public async Task<SyncPushResult> PushAsync(
        string userId,
        SyncChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deviceId = Validate(request.DeviceId, 64, "deviceId");
        var kind = ValidateKind(request.Kind);
        var key = Validate(request.Key, 200, "key");
        if (kind == KindSetting && !PortableSettings.IsAllowed(key))
        {
            throw Invalid("设置项不在可移植白名单内。");
        }

        var idempotencyKey = Validate(request.IdempotencyKey, 128, "idempotencyKey");
        var deleted = request.Deleted == true;
        var baseRevision = request.BaseServerRevision ?? 0;
        if (baseRevision < 0)
        {
            throw Invalid("baseServerRevision 不能为负数。");
        }

        string? payloadJson = null;
        if (!deleted)
        {
            if (request.Payload.ValueKind is not JsonValueKind.Object)
            {
                throw Invalid("payload 必须是 JSON 对象。");
            }

            payloadJson = JsonCanonicalizer.Canonicalize(request.Payload);
            if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            {
                throw Invalid("payload 过大。");
            }
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var replay = await connection.SingleAsync(
                    transaction,
                    """
                    SELECT seq, server_revision, kind, item_key, deleted, payload_json, device_id, created_at
                    FROM sync_changes WHERE user_id = $user AND idempotency_key = $idem
                    """,
                    MapChange,
                    token,
                    ("$user", userId),
                    ("$idem", idempotencyKey)).ConfigureAwait(false);
                if (replay is not null)
                {
                    return new SyncPushResult(ToChange(replay), true);
                }

                var current = await connection.SingleAsync(
                    transaction,
                    """
                    SELECT server_revision, deleted, payload_json FROM sync_items
                    WHERE user_id = $user AND kind = $kind AND item_key = $key
                    """,
                    reader => new CurrentItem(
                        reader.Long("server_revision"),
                        reader.Flag("deleted"),
                        reader.StrOrNull("payload_json")),
                    token,
                    ("$user", userId),
                    ("$kind", kind),
                    ("$key", key)).ConfigureAwait(false);

                var currentRevision = current?.ServerRevision ?? 0;
                if (currentRevision != baseRevision)
                {
                    throw new ApiException(
                        409,
                        "sync.conflict",
                        "同步冲突，请先拉取服务器最新版本。",
                        new
                        {
                            current = current is null
                                ? null
                                : new
                                {
                                    kind,
                                    key,
                                    serverRevision = current.ServerRevision,
                                    deleted = current.Deleted,
                                    payload = ParsePayload(current.PayloadJson),
                                },
                        });
                }

                await connection.ExecuteAsync(
                    transaction,
                    "INSERT INTO sync_counters(user_id, revision) VALUES($user, 0) ON CONFLICT(user_id) DO NOTHING",
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    transaction,
                    "UPDATE sync_counters SET revision = revision + 1 WHERE user_id = $user",
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                var revision = await connection.ScalarInt64Async(
                    transaction,
                    "SELECT revision FROM sync_counters WHERE user_id = $user",
                    token,
                    ("$user", userId)).ConfigureAwait(false);

                await connection.ExecuteAsync(
                    transaction,
                    """
                    INSERT INTO sync_items(user_id, kind, item_key, server_revision, deleted, payload_json, updated_at)
                    VALUES($user, $kind, $key, $revision, $deleted, $payload, $now)
                    ON CONFLICT(user_id, kind, item_key) DO UPDATE SET
                        server_revision = excluded.server_revision,
                        deleted = excluded.deleted,
                        payload_json = excluded.payload_json,
                        updated_at = excluded.updated_at
                    """,
                    token,
                    ("$user", userId),
                    ("$kind", kind),
                    ("$key", key),
                    ("$revision", revision),
                    ("$deleted", deleted ? 1 : 0),
                    ("$payload", payloadJson),
                    ("$now", now)).ConfigureAwait(false);

                await connection.ExecuteAsync(
                    transaction,
                    """
                    INSERT INTO sync_changes(
                        user_id, server_revision, kind, item_key, deleted, payload_json,
                        device_id, idempotency_key, created_at)
                    VALUES($user, $revision, $kind, $key, $deleted, $payload, $device, $idem, $now)
                    """,
                    token,
                    ("$user", userId),
                    ("$revision", revision),
                    ("$kind", kind),
                    ("$key", key),
                    ("$deleted", deleted ? 1 : 0),
                    ("$payload", payloadJson),
                    ("$device", deviceId),
                    ("$idem", idempotencyKey),
                    ("$now", now)).ConfigureAwait(false);

                var seq = await connection.ScalarInt64Async(
                    transaction,
                    "SELECT last_insert_rowid()",
                    token).ConfigureAwait(false);

                await PruneHistoryAsync(connection, transaction, userId, now, token).ConfigureAwait(false);

                var change = new SyncChange(
                    seq,
                    revision,
                    kind,
                    key,
                    deleted,
                    deleted ? null : ParsePayload(payloadJson),
                    deviceId,
                    now);
                return new SyncPushResult(change, false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncPullResult> PullAsync(
        string userId,
        long cursor,
        int limit,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var rows = await database.ReadAsync(
            async (connection, token) =>
            {
                var floor = await connection.ScalarInt64Async(
                    transaction: null,
                    "SELECT COALESCE((SELECT floor_seq FROM sync_floor WHERE user_id = $user), 0)",
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                if (cursor < floor)
                {
                    // The requested history has been pruned; only a snapshot restore can recover.
                    throw new ApiException(
                        410,
                        "sync.cursor_invalid",
                        "同步游标已过期，请先恢复备份快照。",
                        new { requiresSnapshot = true, floor = FormatCursor(floor) });
                }

                return await connection.QueryAsync(
                    transaction: null,
                    """
                    SELECT seq, server_revision, kind, item_key, deleted, payload_json, device_id, created_at
                    FROM sync_changes
                    WHERE user_id = $user AND seq > $cursor
                    ORDER BY seq LIMIT $limit
                    """,
                    MapChange,
                    token,
                    ("$user", userId),
                    ("$cursor", cursor),
                    ("$limit", bounded + 1)).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > bounded;
        var page = hasMore ? rows.Take(bounded).ToList() : rows;
        var nextCursor = page.Count > 0 ? page[^1].Seq : cursor;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var validDevice = Validate(deviceId, 64, "deviceId");
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            await database.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await connection.ExecuteAsync(
                        transaction,
                        """
                        INSERT INTO sync_cursors(user_id, device_id, cursor, updated_at)
                        VALUES($user, $device, $cursor, $now)
                        ON CONFLICT(user_id, device_id) DO UPDATE SET
                            cursor = MAX(sync_cursors.cursor, excluded.cursor),
                            updated_at = excluded.updated_at
                        """,
                        token,
                        ("$user", userId),
                        ("$device", validDevice),
                        ("$cursor", nextCursor),
                        ("$now", now)).ConfigureAwait(false);
                    return 0;
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new SyncPullResult(page.Select(ToChange).ToList(), nextCursor, hasMore);
    }

    /// <summary>
    /// Drops change-log rows past the configured retention window and records the
    /// highest pruned sequence so stale cursors get 410 instead of silently
    /// missing changes.
    /// </summary>
    private async Task PruneHistoryAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string userId,
        long now,
        CancellationToken cancellationToken)
    {
        var cutoff = now - options.SyncHistorySeconds;
        var pruned = await connection.ScalarAsync(
            transaction,
            "SELECT MAX(seq) FROM sync_changes WHERE user_id = $user AND created_at < $cutoff",
            cancellationToken,
            ("$user", userId),
            ("$cutoff", cutoff)).ConfigureAwait(false);
        if (pruned is null)
        {
            return;
        }

        var floor = Convert.ToInt64(pruned, CultureInfo.InvariantCulture);
        await connection.ExecuteAsync(
            transaction,
            "DELETE FROM sync_changes WHERE user_id = $user AND seq <= $floor",
            cancellationToken,
            ("$user", userId),
            ("$floor", floor)).ConfigureAwait(false);
        await connection.ExecuteAsync(
            transaction,
            """
            INSERT INTO sync_floor(user_id, floor_seq, updated_at) VALUES($user, $floor, $now)
            ON CONFLICT(user_id) DO UPDATE SET
                floor_seq = MAX(sync_floor.floor_seq, excluded.floor_seq),
                updated_at = excluded.updated_at
            """,
            cancellationToken,
            ("$user", userId),
            ("$floor", floor),
            ("$now", now)).ConfigureAwait(false);
    }

    private static SyncChange ToChange(ChangeRow row) => new(
        row.Seq,
        row.ServerRevision,
        row.Kind,
        row.Key,
        row.Deleted,
        row.Deleted ? null : ParsePayload(row.PayloadJson),
        row.DeviceId,
        row.CreatedAt);

    private static JsonElement? ParsePayload(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);

    private static ChangeRow MapChange(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.Long("seq"),
        reader.Long("server_revision"),
        reader.Str("kind"),
        reader.Str("item_key"),
        reader.Flag("deleted"),
        reader.StrOrNull("payload_json"),
        reader.Str("device_id"),
        reader.Long("created_at"));

    private static string ValidateKind(string? kind) => kind?.Trim() switch
    {
        KindTask => KindTask,
        KindSetting => KindSetting,
        _ => throw Invalid("kind 只能是 task 或 setting。"),
    };

    private static string Validate(string? value, int maxLength, string name)
    {
        var token = (value ?? string.Empty).Trim();
        if (token.Length is 0 || token.Length > maxLength)
        {
            throw Invalid($"{name} 无效。");
        }

        foreach (var character in token)
        {
            if (char.IsControl(character))
            {
                throw Invalid($"{name} 无效。");
            }
        }

        return token;
    }

    private static ApiException Invalid(string message) => new(400, "sync.invalid", message);

    public static string FormatCursor(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record ChangeRow(
        long Seq,
        long ServerRevision,
        string Kind,
        string Key,
        bool Deleted,
        string? PayloadJson,
        string DeviceId,
        long CreatedAt);

    private sealed record CurrentItem(long ServerRevision, bool Deleted, string? PayloadJson);
}
