using System.Globalization;
using Microsoft.Data.Sqlite;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Projection;

/// <summary>
/// Account-isolated, rebuildable task projection. The card reads it; the only
/// writers are the snapshot bootstrap, the incremental pull and the server's
/// authoritative reply to a single-task command. There is no outbox table and
/// no local edit path by construction.
/// </summary>
public sealed class TaskProjection : IAsyncDisposable
{
    private const string SchemaSql = """
        PRAGMA user_version = 1;

        CREATE TABLE projection_meta (
            singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
            owner_key TEXT NOT NULL,
            schema_version INTEGER NOT NULL CHECK (schema_version = 1),
            baseline_snapshot_id TEXT NULL,
            baseline_server_revision INTEGER NULL,
            baseline_created_at TEXT NULL,
            cursor TEXT NULL,
            restored_at_utc TEXT NOT NULL
        );

        CREATE TABLE tasks (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            revision INTEGER NOT NULL,
            server_revision INTEGER NOT NULL,
            is_completed INTEGER NOT NULL,
            archived_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX tasks_visible ON tasks (archived_at_utc, is_completed, created_at_utc);
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _connection;

    private TaskProjection(SqliteConnection connection)
    {
        _connection = connection;
    }

    public ProjectionBaseline? Baseline { get; private set; }

    public string? Cursor { get; private set; }

    public static async Task<ProjectionOpenResult> OpenAsync(
        ProjectionIdentity identity,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var directory = Path.Combine(rootDirectory, "projections-v3");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, identity.OwnerKey + ".db");

        if (!File.Exists(path))
        {
            var created = await CreateAsync(path, identity, cancellationToken).ConfigureAwait(false);
            return ProjectionOpenResult.Opened(created);
        }

        SqliteConnection connection;
        try
        {
            connection = await OpenConnectionAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            Quarantine(path);
            return ProjectionOpenResult.Failed(ProjectionOpenStatus.Corrupt, "projection.unreadable");
        }

        try
        {
            var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version != 1)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                Quarantine(path);
                return ProjectionOpenResult.Failed(ProjectionOpenStatus.SchemaMismatch, "projection.schema");
            }

            var meta = await ReadMetaAsync(connection, cancellationToken).ConfigureAwait(false);
            if (meta is null || !string.Equals(meta.OwnerKey, identity.OwnerKey, StringComparison.Ordinal))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                Quarantine(path);
                return ProjectionOpenResult.Failed(ProjectionOpenStatus.OwnerMismatch, "projection.owner");
            }

            var projection = new TaskProjection(connection)
            {
                Baseline = meta.BaselineSnapshotId is null
                    ? null
                    : new ProjectionBaseline(
                        meta.BaselineSnapshotId,
                        meta.BaselineServerRevision ?? 0,
                        meta.BaselineCreatedAt ?? meta.RestoredAtUtc,
                        meta.Cursor,
                        meta.RestoredAtUtc),
                Cursor = meta.Cursor,
            };
            return ProjectionOpenResult.Opened(projection);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or FormatException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            Quarantine(path);
            return ProjectionOpenResult.Failed(ProjectionOpenStatus.Corrupt, "projection.corrupt");
        }
    }

    public async Task ReplaceAsync(
        IReadOnlyList<SyncedTask> tasks,
        ProjectionBaseline baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(baseline);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("DELETE FROM tasks;", transaction, cancellationToken).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                await UpsertAsync(task, transaction, cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(
                """
                UPDATE projection_meta SET
                    baseline_snapshot_id = $snapshot,
                    baseline_server_revision = $serverRevision,
                    baseline_created_at = $createdAt,
                    cursor = $cursor,
                    restored_at_utc = $restoredAt
                WHERE singleton = 1;
                """,
                transaction,
                cancellationToken,
                ("$snapshot", baseline.SnapshotId),
                ("$serverRevision", baseline.ServerRevision),
                ("$createdAt", WireTime(baseline.SnapshotCreatedAt)),
                ("$cursor", baseline.Cursor),
                ("$restoredAt", WireTime(baseline.RestoredAtUtc))).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            Baseline = baseline;
            Cursor = baseline.Cursor;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies one page of server changes. Only <c>task</c> entries are applied;
    /// settings are ignored because this client exposes no settings surface.
    /// A server tombstone removes the row — that is the server's deletion, not a
    /// tombstone this client ever writes.
    /// </summary>
    public async Task ApplyChangesAsync(
        IReadOnlyList<SyncChangeDto> changes,
        string? nextCursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var change in changes)
            {
                await ApplyChangeAsync(change, transaction, cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync("UPDATE projection_meta SET cursor = $cursor WHERE singleton = 1;", transaction, cancellationToken, ("$cursor", nextCursor)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Cursor = nextCursor;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies the server's authoritative reply to a single-task command.</summary>
    public async Task ApplyServerTaskAsync(SyncChangeDto change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ApplyChangeAsync(change, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncedTask?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, title, payload_json, revision, server_revision, is_completed, archived_at_utc, created_at_utc FROM tasks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Every task the widget may show, archived ones included. The card's own
    /// <see cref="ListVisibleAsync"/> stays limited to unarchived rows; this read
    /// exists only so a widget configured to the archived or completed group can
    /// be filled. Rows are returned newest-created first and capped, because the
    /// update time used for ordering lives in the payload, not in a column.
    /// </summary>
    public async Task<IReadOnlyList<SyncedTask>> ListForWidgetAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, payload_json, revision, server_revision, is_completed, archived_at_utc, created_at_utc
                FROM tasks
                ORDER BY created_at_utc DESC, id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<SyncedTask>(Math.Min(limit, 16));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(Map(reader));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncedTask>> ListVisibleAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, payload_json, revision, server_revision, is_completed, archived_at_utc, created_at_utc
                FROM tasks
                WHERE archived_at_utc IS NULL
                ORDER BY is_completed ASC, created_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<SyncedTask>(Math.Min(limit, 16));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(Map(reader));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectionCounts> CountAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN archived_at_utc IS NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN archived_at_utc IS NULL AND is_completed = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN archived_at_utc IS NOT NULL THEN 1 ELSE 0 END), 0)
                FROM tasks;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new ProjectionCounts(0, 0, 0);
            }

            return new ProjectionCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static async Task<TaskProjection> CreateAsync(string path, ProjectionIdentity identity, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(path, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO projection_meta (singleton, owner_key, schema_version, restored_at_utc)
                VALUES (1, $owner, 1, $restoredAt);
                """;
            command.Parameters.AddWithValue("$owner", identity.OwnerKey);
            command.Parameters.AddWithValue("$restoredAt", WireTime(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TaskProjection(connection);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string path, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10,
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<MetaRecord?> ReadMetaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_key, baseline_snapshot_id, baseline_server_revision, baseline_created_at, cursor, restored_at_utc
            FROM projection_meta WHERE singleton = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MetaRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : ParseWireTime(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            ParseWireTime(reader.GetString(5)));
    }

    private async Task ApplyChangeAsync(SyncChangeDto change, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (!string.Equals(change.Kind?.Trim(), SyncKinds.Task, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(change.Key, out var id))
        {
            return;
        }

        if (change.Deleted)
        {
            await ExecuteAsync("DELETE FROM tasks WHERE id = $id;", transaction, cancellationToken, ("$id", id.ToString("D", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return;
        }

        var payloadJson = change.Payload?.GetRawText();
        if (!TaskPayload.TryRead(payloadJson, out var fields, out var error) || fields.Id != id)
        {
            throw new InvalidOperationException($"Server task change could not be applied: {error ?? "payload.mismatch"}.");
        }

        await UpsertAsync(
            new SyncedTask
            {
                Id = id,
                Title = fields.Title,
                PayloadJson = payloadJson!,
                Revision = fields.Revision,
                ServerRevision = change.ServerRevision,
                IsCompleted = fields.IsCompleted,
                ArchivedAtUtc = fields.ArchivedAtUtc,
                CreatedAtUtc = fields.CreatedAtUtc,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(SyncedTask task, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            """
            INSERT INTO tasks (id, title, payload_json, revision, server_revision, is_completed, archived_at_utc, created_at_utc)
            VALUES ($id, $title, $payload, $revision, $serverRevision, $isCompleted, $archivedAt, $createdAt)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                payload_json = excluded.payload_json,
                revision = excluded.revision,
                server_revision = excluded.server_revision,
                is_completed = excluded.is_completed,
                archived_at_utc = excluded.archived_at_utc,
                created_at_utc = excluded.created_at_utc;
            """,
            transaction,
            cancellationToken,
            ("$id", task.Id.ToString("D", CultureInfo.InvariantCulture)),
            ("$title", task.Title),
            ("$payload", task.PayloadJson),
            ("$revision", task.Revision),
            ("$serverRevision", task.ServerRevision),
            ("$isCompleted", task.IsCompleted ? 1 : 0),
            ("$archivedAt", task.ArchivedAtUtc is { } archived ? WireTime(archived) : null),
            ("$createdAt", WireTime(task.CreatedAtUtc))).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        string sql,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SyncedTask Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        PayloadJson = reader.GetString(2),
        Revision = reader.GetInt64(3),
        ServerRevision = reader.GetInt64(4),
        IsCompleted = reader.GetInt64(5) != 0,
        ArchivedAtUtc = reader.IsDBNull(6) ? null : ParseWireTime(reader.GetString(6)),
        CreatedAtUtc = ParseWireTime(reader.GetString(7)),
    };

    private static string WireTime(DateTimeOffset value) => TaskPayload.Format(value);

    private static DateTimeOffset ParseWireTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void Quarantine(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is null)
            {
                return;
            }

            var quarantineDirectory = Path.Combine(directory, "quarantine");
            Directory.CreateDirectory(quarantineDirectory);
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm" })
            {
                var source = Path.Combine(directory, name + suffix);
                if (File.Exists(source))
                {
                    File.Move(source, Path.Combine(quarantineDirectory, $"{name}-{stamp}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}{suffix}"));
                }
            }
        }
        catch (IOException)
        {
            // Best effort: a file that cannot be moved is still never opened again
            // by this run because the caller refuses to continue without an owner match.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record MetaRecord(
        string OwnerKey,
        string? BaselineSnapshotId,
        long? BaselineServerRevision,
        DateTimeOffset? BaselineCreatedAt,
        string? Cursor,
        DateTimeOffset RestoredAtUtc);
}