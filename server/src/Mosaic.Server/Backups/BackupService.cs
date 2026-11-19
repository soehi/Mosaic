using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mosaic.Server.Data;
using Mosaic.Server.Http;

namespace Mosaic.Server.Backups;

public sealed record BackupItemRequest(string? Kind, string? Key, long Revision, JsonElement Payload);

public sealed record BackupUploadRequest(
    string? DeviceId,
    string? BaseSnapshotId,
    string? ContentSha256,
    List<BackupItemRequest>? Items);

public sealed record BackupUploadResult(
    string SnapshotId,
    long CreatedAt,
    string ContentSha256,
    long SizeBytes,
    int ItemCount,
    long ServerRevision,
    long Cursor,
    bool Duplicate);

public sealed record BackupManifest(
    string SnapshotId,
    long CreatedAt,
    string? BaseSnapshotId,
    string ContentSha256,
    long SizeBytes,
    int ItemCount,
    long ServerRevision,
    long Cursor,
    IReadOnlyList<BackupManifestItem> Items);

public sealed record BackupManifestItem(string Kind, string Key, long Revision);

public sealed record BackupSnapshot(BackupManifest Manifest, IReadOnlyList<BackupSnapshotItem> Items);

public sealed record BackupSnapshotItem(string Kind, string Key, long Revision, JsonElement Payload);

/// <summary>
/// Immutable snapshots of tasks and portable user settings. Content is hashed
/// with SHA-256 over the canonical form, snapshots are de-duplicated, and the
/// user's total stored bytes are capped by the configured quota.
/// </summary>
public sealed class BackupService(Database database, ServerOptions options, TimeProvider timeProvider)
{
    public const long MaxItemPayloadBytes = 1024 * 1024;

    private const string KindTask = "task";

    private const string KindSetting = "setting";

    public async Task<BackupUploadResult> UploadAsync(
        string userId,
        BackupUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deviceId = ValidateToken(request.DeviceId, 64, "deviceId");
        var baseSnapshotId = string.IsNullOrWhiteSpace(request.BaseSnapshotId)
            ? null
            : ValidateToken(request.BaseSnapshotId, 64, "baseSnapshotId");
        var items = request.Items ?? [];
        if (items.Count > options.MaxItemsPerBackup)
        {
            throw new ApiException(400, "backup.invalid", $"一次最多上传 {options.MaxItemsPerBackup} 个条目。");
        }

        var canonical = new List<CanonicalItem>(items.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var kind = ValidateKind(item.Kind);
            var key = ValidateToken(item.Key, 200, "key");
            if (kind == KindSetting && !Mosaic.Server.Sync.PortableSettings.IsAllowed(key))
            {
                throw new ApiException(400, "backup.invalid", "设置项不在可移植白名单内。");
            }

            if (!keys.Add(kind + "\u0000" + key))
            {
                throw new ApiException(400, "backup.invalid", "备份中存在重复条目。");
            }

            if (item.Revision < 0)
            {
                throw new ApiException(400, "backup.invalid", "revision 不能为负数。");
            }

            if (item.Payload.ValueKind is not JsonValueKind.Object)
            {
                throw new ApiException(400, "backup.invalid", "payload 必须是 JSON 对象。");
            }

            var payloadJson = JsonCanonicalizer.Canonicalize(item.Payload);
            if (Encoding.UTF8.GetByteCount(payloadJson) > MaxItemPayloadBytes)
            {
                throw new ApiException(400, "backup.invalid", "单个条目内容过大。");
            }

            canonical.Add(new CanonicalItem(kind, key, item.Revision, payloadJson));
        }

        canonical.Sort((left, right) =>
        {
            var kindOrder = string.CompareOrdinal(left.Kind, right.Kind);
            return kindOrder != 0 ? kindOrder : string.CompareOrdinal(left.Key, right.Key);
        });

        var content = JsonCanonicalizer.SerializeBackupContent(canonical);
        var contentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(request.ContentSha256)
            && !string.Equals(contentHash, request.ContentSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ApiException(400, "backup.hash_mismatch", "备份内容校验失败，请重新上传。");
        }

        var sizeBytes = content.LongLength;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var snapshotId = Guid.NewGuid().ToString("N");

        try
        {
            return await database.WriteAsync(
                async (connection, transaction, token) =>
                {
                    if (baseSnapshotId is not null)
                    {
                        var baseExists = await connection.ScalarInt64Async(
                            transaction,
                            "SELECT COUNT(*) FROM backups WHERE id = $id AND user_id = $user",
                            token,
                            ("$id", baseSnapshotId),
                            ("$user", userId)).ConfigureAwait(false);
                        if (baseExists == 0)
                        {
                            throw new ApiException(404, "backup.not_found", "找不到基准备份。");
                        }
                    }

                    var latest = await connection.SingleAsync(
                        transaction,
                        """
                        SELECT id, content_sha256, created_at, size_bytes, item_count,
                               server_revision, sync_cursor
                        FROM backups
                        WHERE user_id = $user
                        ORDER BY created_at DESC, rowid DESC LIMIT 1
                        """,
                        reader => new LatestRow(
                            reader.Str("id"),
                            reader.Str("content_sha256"),
                            reader.Long("created_at"),
                            reader.Long("size_bytes"),
                            (int)reader.Long("item_count"),
                            reader.Long("server_revision"),
                            reader.Long("sync_cursor")),
                        token,
                        ("$user", userId)).ConfigureAwait(false);
                    if (latest is not null && string.Equals(latest.ContentSha256, contentHash, StringComparison.Ordinal))
                    {
                        return new BackupUploadResult(
                            latest.Id,
                            latest.CreatedAt,
                            latest.ContentSha256,
                            latest.SizeBytes,
                            latest.ItemCount,
                            latest.ServerRevision,
                            latest.Cursor,
                            true);
                    }

                    // Stamp the snapshot with the sync position it corresponds to so a
                    // restoring client knows where its incremental cursor may resume.
                    var serverRevision = await connection.ScalarInt64Async(
                        transaction,
                        "SELECT COALESCE((SELECT revision FROM sync_counters WHERE user_id = $user), 0)",
                        token,
                        ("$user", userId)).ConfigureAwait(false);
                    var cursor = await connection.ScalarInt64Async(
                        transaction,
                        "SELECT COALESCE((SELECT MAX(seq) FROM sync_changes WHERE user_id = $user), 0)",
                        token,
                        ("$user", userId)).ConfigureAwait(false);

                    // Quota is measured after retention: adding a snapshot may evict the oldest one.
                    var keep = Math.Max(0, options.BackupRetention - 1);
                    var retainedBytes = await connection.ScalarInt64Async(
                        transaction,
                        """
                        SELECT COALESCE(SUM(size_bytes), 0) FROM backups WHERE user_id = $user
                        AND id IN (SELECT id FROM backups WHERE user_id = $user
                                   ORDER BY created_at DESC, rowid DESC LIMIT $keep)
                        """,
                        token,
                        ("$user", userId),
                        ("$keep", keep)).ConfigureAwait(false);
                    if (retainedBytes + sizeBytes > options.BackupQuotaBytes)
                    {
                        throw new ApiException(413, "backup.quota_exceeded", "备份空间已满，请先清理旧备份。");
                    }

                    await connection.ExecuteAsync(
                        transaction,
                        """
                        INSERT INTO backups(id, user_id, device_id, base_snapshot_id, content_sha256,
                                            created_at, size_bytes, item_count, server_revision, sync_cursor)
                        VALUES($id, $user, $device, $base, $hash, $now, $size, $count, $revision, $cursor)
                        """,
                        token,
                        ("$id", snapshotId),
                        ("$user", userId),
                        ("$device", deviceId),
                        ("$base", baseSnapshotId),
                        ("$hash", contentHash),
                        ("$now", now),
                        ("$size", sizeBytes),
                        ("$count", canonical.Count),
                        ("$revision", serverRevision),
                        ("$cursor", cursor)).ConfigureAwait(false);

                    foreach (var item in canonical)
                    {
                        await connection.ExecuteAsync(
                            transaction,
                            """
                            INSERT INTO backup_items(id, backup_id, kind, item_key, payload_json, revision)
                            VALUES($id, $backup, $kind, $key, $payload, $revision)
                            """,
                            token,
                            ("$id", Guid.NewGuid().ToString("N")),
                            ("$backup", snapshotId),
                            ("$kind", item.Kind),
                            ("$key", item.Key),
                            ("$payload", item.PayloadJson),
                            ("$revision", item.Revision)).ConfigureAwait(false);
                    }

                    await connection.ExecuteAsync(
                        transaction,
                        """
                        DELETE FROM backups WHERE user_id = $user AND id IN (
                            SELECT id FROM backups WHERE user_id = $user
                            ORDER BY created_at DESC, rowid DESC LIMIT -1 OFFSET $retention)
                        """,
                        token,
                        ("$user", userId),
                        ("$retention", options.BackupRetention)).ConfigureAwait(false);

                    return new BackupUploadResult(
                        snapshotId,
                        now,
                        contentHash,
                        sizeBytes,
                        canonical.Count,
                        serverRevision,
                        cursor,
                        false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ApiException(409, "backup.conflict", "备份写入冲突，请重试。");
        }
    }

    public async Task<BackupManifest?> GetLatestAsync(string userId, CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) =>
            {
                var header = await connection.SingleAsync(
                    transaction: null,
                    """
                    SELECT id, created_at, base_snapshot_id, content_sha256, size_bytes, item_count,
                           server_revision, sync_cursor
                    FROM backups WHERE user_id = $user
                    ORDER BY created_at DESC, rowid DESC LIMIT 1
                    """,
                    MapManifestHeader,
                    token,
                    ("$user", userId)).ConfigureAwait(false);
                return header is null ? null : await LoadManifestAsync(connection, null, header, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<BackupSnapshot?> GetAsync(
        string userId,
        string snapshotId,
        CancellationToken cancellationToken) =>
        await database.ReadAsync(
            async (connection, token) =>
            {
                var header = await connection.SingleAsync(
                    transaction: null,
                    """
                    SELECT id, created_at, base_snapshot_id, content_sha256, size_bytes, item_count,
                           server_revision, sync_cursor
                    FROM backups WHERE id = $id AND user_id = $user
                    """,
                    MapManifestHeader,
                    token,
                    ("$id", snapshotId),
                    ("$user", userId)).ConfigureAwait(false);
                if (header is null)
                {
                    return null;
                }

                var items = await connection.QueryAsync(
                    transaction: null,
                    """
                    SELECT kind, item_key, revision, payload_json FROM backup_items
                    WHERE backup_id = $id ORDER BY kind, item_key
                    """,
                    reader => new CanonicalItem(
                        reader.Str("kind"),
                        reader.Str("item_key"),
                        reader.Long("revision"),
                        reader.Str("payload_json")),
                    token,
                    ("$id", snapshotId)).ConfigureAwait(false);

                var content = JsonCanonicalizer.SerializeBackupContent(items);
                var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!string.Equals(hash, header.ContentSha256, StringComparison.Ordinal))
                {
                    throw new ApiException(500, "backup.corrupt", "备份内容校验失败，请联系服务端管理员。");
                }

                var manifest = new BackupManifest(
                    header.Id,
                    header.CreatedAt,
                    header.BaseSnapshotId,
                    header.ContentSha256,
                    header.SizeBytes,
                    header.ItemCount,
                    header.ServerRevision,
                    header.Cursor,
                    items.Select(item => new BackupManifestItem(item.Kind, item.Key, item.Revision)).ToList());
                var payloads = items.Select(item => new BackupSnapshotItem(
                    item.Kind,
                    item.Key,
                    item.Revision,
                    JsonSerializer.Deserialize<JsonElement>(item.PayloadJson))).ToList();
                return new BackupSnapshot(manifest, payloads);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task RecordRestoreAsync(
        string userId,
        string snapshotId,
        CancellationToken cancellationToken) =>
        await database.WriteAsync(
            async (connection, transaction, token) =>
            {
                await connection.ExecuteAsync(
                    transaction,
                    "INSERT INTO settings_audit(user_id, action, created_at) VALUES($user, $action, $now)",
                    token,
                    ("$user", userId),
                    ("$action", "backup.restore:" + snapshotId),
                    ("$now", timeProvider.GetUtcNow().ToUnixTimeSeconds())).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);

    private static async Task<BackupManifest> LoadManifestAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        ManifestHeader header,
        CancellationToken cancellationToken)
    {
        var items = await connection.QueryAsync(
            transaction,
            "SELECT kind, item_key, revision FROM backup_items WHERE backup_id = $id ORDER BY kind, item_key",
            reader => new BackupManifestItem(reader.Str("kind"), reader.Str("item_key"), reader.Long("revision")),
            cancellationToken,
            ("$id", header.Id)).ConfigureAwait(false);
        return new BackupManifest(
            header.Id,
            header.CreatedAt,
            header.BaseSnapshotId,
            header.ContentSha256,
            header.SizeBytes,
            header.ItemCount,
            header.ServerRevision,
            header.Cursor,
            items);
    }

    private static ManifestHeader MapManifestHeader(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            reader.Str("id"),
            reader.Long("created_at"),
            reader.StrOrNull("base_snapshot_id"),
            reader.Str("content_sha256"),
            reader.Long("size_bytes"),
            (int)reader.Long("item_count"),
            reader.Long("server_revision"),
            reader.Long("sync_cursor"));

    private static string ValidateKind(string? kind) => kind?.Trim() switch
    {
        KindTask => KindTask,
        KindSetting => KindSetting,
        _ => throw new ApiException(400, "backup.invalid", "备份条目类型无效。"),
    };

    private static string ValidateToken(string? value, int maxLength, string name)
    {
        var token = (value ?? string.Empty).Trim();
        if (token.Length is 0 || token.Length > maxLength)
        {
            throw new ApiException(400, "backup.invalid", $"{name} 无效。");
        }

        foreach (var character in token)
        {
            if (char.IsControl(character))
            {
                throw new ApiException(400, "backup.invalid", $"{name} 无效。");
            }
        }

        return token;
    }

    private sealed record LatestRow(
        string Id,
        string ContentSha256,
        long CreatedAt,
        long SizeBytes,
        int ItemCount,
        long ServerRevision,
        long Cursor);

    private sealed record ManifestHeader(
        string Id,
        long CreatedAt,
        string? BaseSnapshotId,
        string ContentSha256,
        long SizeBytes,
        int ItemCount,
        long ServerRevision,
        long Cursor);
}
