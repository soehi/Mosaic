using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

/// <summary>
/// SQLite projection. A page is applied in one transaction together with its
/// cursor, so a crash can never leave "rows newer than the cursor" or the
/// reverse. Rows are last-writer-wins by <c>serverRevision</c>, which also
/// makes a replayed page idempotent.
/// </summary>
public sealed class SqliteSyncStore : ISyncStore
{
    private const string CursorKey = "sync.cursor";
    private const string LastSyncedKey = "sync.last_synced_at";
    private const string MaxRevisionKey = "sync.max_server_revision";

    private readonly MobileDatabase _database;

    public SqliteSyncStore(MobileDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<SyncState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyPageAsync(SyncPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var task in page.Tasks.OrderBy(t => t.ServerRevision))
        {
            await UpsertTaskAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tombstone in page.Tombstones.OrderBy(t => t.ServerRevision))
        {
            await ApplyTombstoneAsync(connection, transaction, tombstone, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var setting in page.Settings.OrderBy(s => s.ServerRevision))
        {
            await UpsertSettingAsync(connection, transaction, setting, cancellationToken).ConfigureAwait(false);
        }

        var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var maxRevision = page.Tasks.Select(t => t.ServerRevision)
            .Concat(page.Tombstones.Select(t => t.ServerRevision))
            .Concat(page.Settings.Select(s => s.ServerRevision))
            .DefaultIfEmpty(state.MaxServerRevision)
            .Max();

        await WriteMetaAsync(connection, transaction, CursorKey, page.NextCursor ?? state.Cursor, cancellationToken)
            .ConfigureAwait(false);
        await WriteMetaAsync(connection, transaction, LastSyncedKey, FormatDate(page.SyncedAt), cancellationToken)
            .ConfigureAwait(false);
        await WriteMetaAsync(
                connection,
                transaction,
                MaxRevisionKey,
                Math.Max(state.MaxServerRevision, maxRevision).ToString(CultureInfo.InvariantCulture),
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetProjectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM tasks;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM task_tombstones;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM settings;", cancellationToken).ConfigureAwait(false);
        await DeleteMetaAsync(connection, transaction, CursorKey, cancellationToken).ConfigureAwait(false);
        await DeleteMetaAsync(connection, transaction, MaxRevisionKey, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM tasks;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM task_tombstones;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM settings;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM outbox;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM meta;", cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceProjectionAsync(
        IReadOnlyList<SyncedTask> tasks,
        IReadOnlyList<StoredSetting> settings,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM tasks;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM task_tombstones;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM settings;", cancellationToken).ConfigureAwait(false);

        foreach (var task in tasks)
        {
            await UpsertTaskAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
        }

        foreach (var setting in settings)
        {
            await UpsertSettingAsync(connection, transaction, setting, cancellationToken).ConfigureAwait(false);
        }

        var maxRevision = tasks.Select(t => t.ServerRevision)
            .Concat(settings.Select(s => s.ServerRevision))
            .DefaultIfEmpty(0)
            .Max();
        await WriteMetaAsync(connection, transaction, CursorKey, cursor, cancellationToken).ConfigureAwait(false);
        await WriteMetaAsync(
                connection,
                transaction,
                LastSyncedKey,
                FormatDate(DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteMetaAsync(
                connection,
                transaction,
                MaxRevisionKey,
                maxRevision.ToString(CultureInfo.InvariantCulture),
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncedTask>> ListTasksAsync(
        MobileTaskFilter filter,
        CancellationToken cancellationToken = default)
    {
        var (where, order) = filter switch
        {
            MobileTaskFilter.Open => ("archived_at_utc IS NULL AND is_completed = 0", "created_at_utc DESC"),
            MobileTaskFilter.Completed => ("archived_at_utc IS NULL AND is_completed = 1", "completed_at_utc DESC"),
            MobileTaskFilter.Archived => ("archived_at_utc IS NOT NULL", "archived_at_utc DESC"),
            _ => ("archived_at_utc IS NULL", "created_at_utc DESC"),
        };

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, title, notes, is_completed, created_at_utc, updated_at_utc, revision,
                   completed_at_utc, archived_at_utc, restored_at_utc, server_revision, device_id
            FROM tasks
            WHERE {where}
            ORDER BY {order};
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SyncedTask>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadTask(reader));
        }

        return results;
    }

    public async Task<SyncedTask?> GetTaskAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, notes, is_completed, created_at_utc, updated_at_utc, revision,
                   completed_at_utc, archived_at_utc, restored_at_utc, server_revision, device_id
            FROM tasks WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<StoredSetting>> ListSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_json, server_revision, updated_at_utc FROM settings ORDER BY key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<StoredSetting>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new StoredSetting(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3))));
        }

        return results;
    }

    public async Task<int> CountTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tasks;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task UpsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncedTask task,
        CancellationToken cancellationToken)
    {
        // A tombstone newer than the incoming upsert wins; otherwise an older
        // page replayed after the delete would resurrect the task.
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT server_revision FROM task_tombstones WHERE id = $id;";
            check.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            var buried = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (buried is not null
                && buried is not DBNull
                && Convert.ToInt64(buried, CultureInfo.InvariantCulture) > task.ServerRevision)
            {
                return;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks (
                id, title, notes, is_completed, created_at_utc, updated_at_utc, revision,
                completed_at_utc, archived_at_utc, restored_at_utc, server_revision, device_id)
            VALUES ($id, $title, $notes, $completed, $created, $updated, $revision,
                    $completedAt, $archivedAt, $restoredAt, $serverRevision, $deviceId)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                notes = excluded.notes,
                is_completed = excluded.is_completed,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc,
                revision = excluded.revision,
                completed_at_utc = excluded.completed_at_utc,
                archived_at_utc = excluded.archived_at_utc,
                restored_at_utc = excluded.restored_at_utc,
                server_revision = excluded.server_revision,
                device_id = excluded.device_id
            WHERE excluded.server_revision >= tasks.server_revision;
            """;
        command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$completed", task.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$created", FormatDate(task.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", FormatDate(task.UpdatedAtUtc));
        command.Parameters.AddWithValue("$revision", task.Revision);
        command.Parameters.AddWithValue("$completedAt", FormatNullable(task.CompletedAtUtc));
        command.Parameters.AddWithValue("$archivedAt", FormatNullable(task.ArchivedAtUtc));
        command.Parameters.AddWithValue("$restoredAt", FormatNullable(task.RestoredAtUtc));
        command.Parameters.AddWithValue("$serverRevision", task.ServerRevision);
        command.Parameters.AddWithValue("$deviceId", (object?)task.DeviceId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // An upsert wins over an older tombstone.
        await using var unbury = connection.CreateCommand();
        unbury.Transaction = transaction;
        unbury.CommandText = "DELETE FROM task_tombstones WHERE id = $id AND server_revision <= $serverRevision;";
        unbury.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        unbury.Parameters.AddWithValue("$serverRevision", task.ServerRevision);
        await unbury.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TaskTombstone tombstone,
        CancellationToken cancellationToken)
    {
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM tasks WHERE id = $id;";
            remove.Parameters.AddWithValue("$id", tombstone.Id.ToString("D"));
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO task_tombstones (id, server_revision, deleted_at_utc)
            VALUES ($id, $serverRevision, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET
                server_revision = excluded.server_revision,
                deleted_at_utc = excluded.deleted_at_utc
            WHERE excluded.server_revision >= task_tombstones.server_revision;
            """;
        insert.Parameters.AddWithValue("$id", tombstone.Id.ToString("D"));
        insert.Parameters.AddWithValue("$serverRevision", tombstone.ServerRevision);
        insert.Parameters.AddWithValue("$deletedAt", FormatNullable(tombstone.DeletedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredSetting setting,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings (key, value_json, server_revision, updated_at_utc)
            VALUES ($key, $value, $serverRevision, $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                server_revision = excluded.server_revision,
                updated_at_utc = excluded.updated_at_utc
            WHERE excluded.server_revision >= settings.server_revision;
            """;
        command.Parameters.AddWithValue("$key", setting.Key);
        command.Parameters.AddWithValue("$value", setting.ValueJson);
        command.Parameters.AddWithValue("$serverRevision", setting.ServerRevision);
        command.Parameters.AddWithValue("$updatedAt", FormatNullable(setting.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SyncState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var cursor = await ReadMetaAsync(connection, transaction, CursorKey, cancellationToken).ConfigureAwait(false);
        var lastSynced = await ReadMetaAsync(connection, transaction, LastSyncedKey, cancellationToken)
            .ConfigureAwait(false);
        var maxRevision = await ReadMetaAsync(connection, transaction, MaxRevisionKey, cancellationToken)
            .ConfigureAwait(false);
        return new SyncState(
            cursor,
            lastSynced is null ? null : ParseDate(lastSynced),
            long.TryParse(maxRevision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0);
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task WriteMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            // "No cursor" must read back as null, not as an empty string.
            await DeleteMetaAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SyncedTask ReadTask(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        Notes = reader.GetString(2),
        IsCompleted = reader.GetInt32(3) != 0,
        CreatedAtUtc = ParseDate(reader.GetString(4)),
        UpdatedAtUtc = ParseDate(reader.GetString(5)),
        Revision = reader.GetInt64(6),
        CompletedAtUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
        ArchivedAtUtc = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
        RestoredAtUtc = reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
        ServerRevision = reader.GetInt64(10),
        DeviceId = reader.IsDBNull(11) ? null : reader.GetString(11),
    };

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullable(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatDate(value.Value);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
