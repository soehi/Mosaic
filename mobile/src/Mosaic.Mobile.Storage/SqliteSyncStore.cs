using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

/// <summary>
/// Account-isolated SQLite projection. Every page commits tasks, tombstones,
/// cursor and page timestamps in one transaction. Server revision is the only
/// ordering authority.
/// </summary>
public sealed class SqliteSyncStore : ISyncStore
{
    private readonly MobileDatabase _database;

    public SqliteSyncStore(MobileDatabase database, ProjectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(identity);
        _database = database;
        Identity = identity;
    }

    public ProjectionIdentity Identity { get; }

    public async Task<SyncState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAttemptAsync(
        ProjectionFreshness freshness,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE projection_meta
            SET last_attempt_at = $at, freshness = $freshness
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue("$at", FormatDate(at));
        command.Parameters.AddWithValue("$freshness", FreshnessKey(freshness));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyPageAsync(SyncPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var nextCursor = ProjectionCursor.Validate(page.NextCursor);
        ValidatePageTasks(page.Tasks);
        ValidatePageTombstones(page.Tombstones);
        var finalTasks = AggregateTasks(page.Tasks, page.Tombstones);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (page.ExpectedCursor is not null
            && !string.Equals(state.Cursor, page.ExpectedCursor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The projection cursor changed before this page was committed.");
        }

        if (state.Cursor is not null
            && string.Equals(state.Cursor, nextCursor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The next cursor repeats the current cursor.");
        }

        foreach (var task in finalTasks.Values.OrderBy(t => t.Id))
        {
            await UpsertTaskAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tombstone in page.Tombstones.OrderBy(t => t.Id).ThenBy(t => t.ServerRevision))
        {
            await ApplyTombstoneAsync(connection, transaction, tombstone, cancellationToken)
                .ConfigureAwait(false);
        }

        var maxRevision = finalTasks.Values.Select(t => t.ServerRevision)
            .Concat(page.Tombstones.Select(t => t.ServerRevision))
            .DefaultIfEmpty(state.MaxServerRevision)
            .Max();
        await using var meta = connection.CreateCommand();
        meta.Transaction = transaction;
        // A committed page is not a completed run. Only CompleteSyncAsync may
        // declare the projection ready, so HasMore=false alone cannot mark it.
        meta.CommandText = """
            UPDATE projection_meta
            SET cursor = $cursor,
                max_server_revision = $maxRevision,
                last_attempt_at = $at,
                last_successful_page_at = $at,
                freshness = 'syncing'
            WHERE singleton = 1;
            """;
        meta.Parameters.AddWithValue("$cursor", nextCursor);
        meta.Parameters.AddWithValue("$maxRevision", Math.Max(state.MaxServerRevision, maxRevision));
        meta.Parameters.AddWithValue("$at", FormatDate(page.SyncedAt));
        await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSyncAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE projection_meta
            SET last_complete_sync_at = $at, freshness = 'ready'
            WHERE singleton = 1 AND cursor IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$at", FormatDate(at));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        // Clearing keeps the owned projection file and its validated schema in
        // place. Deleting the file would leave a live store pointing at an
        // uninitialised database and could race a concurrent open.
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM tasks;
            DELETE FROM task_tombstones;
            UPDATE projection_meta
            SET cursor = NULL,
                baseline_snapshot_id = NULL,
                baseline_snapshot_server_revision = NULL,
                baseline_snapshot_created_at = NULL,
                max_server_revision = 0,
                last_attempt_at = NULL,
                last_complete_sync_at = NULL,
                last_successful_page_at = NULL,
                freshness = 'unverified'
            WHERE singleton = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceProjectionAsync(
        IReadOnlyList<SyncedTask> tasks,
        ProjectionBaseline baseline,
        DateTimeOffset restoredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(baseline);
        var cursor = ProjectionCursor.Validate(baseline.Cursor);
        ValidatePageTasks(tasks);
        if (tasks.Select(t => t.Id).Distinct().Count() != tasks.Count)
        {
            throw new ArgumentException("The snapshot contains duplicate task ids.", nameof(tasks));
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM tasks; DELETE FROM task_tombstones;";
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var task in tasks.OrderBy(t => t.Id))
        {
            await UpsertTaskAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
        }

        await using var meta = connection.CreateCommand();
        meta.Transaction = transaction;
        // A verified snapshot is a baseline, not a finished incremental run:
        // completion timestamps stay empty and the state remains bootstrapping
        // until the change stream from this cursor catches up.
        meta.CommandText = """
            UPDATE projection_meta
            SET cursor = $cursor,
                baseline_snapshot_id = $snapshot,
                baseline_snapshot_server_revision = $revision,
                baseline_snapshot_created_at = $created,
                max_server_revision = $maxRevision,
                last_attempt_at = $at,
                last_complete_sync_at = NULL,
                last_successful_page_at = NULL,
                freshness = 'bootstrapping'
            WHERE singleton = 1;
            """;
        meta.Parameters.AddWithValue("$cursor", cursor);
        meta.Parameters.AddWithValue("$snapshot", baseline.SnapshotId);
        meta.Parameters.AddWithValue("$revision", baseline.ServerRevision);
        meta.Parameters.AddWithValue("$created", FormatDate(baseline.CreatedAtUtc));
        meta.Parameters.AddWithValue("$maxRevision", baseline.ServerRevision);
        meta.Parameters.AddWithValue("$at", FormatDate(restoredAt));
        await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncedTask>> ListTasksAsync(
        MobileTaskFilter filter,
        CancellationToken cancellationToken = default)
    {
        var (where, order) = filter switch
        {
            MobileTaskFilter.Open =>
                ("archived_at_utc IS NULL AND is_completed = 0", "created_at_utc DESC, id"),
            MobileTaskFilter.Completed =>
                ("archived_at_utc IS NULL AND is_completed = 1", "completed_at_utc DESC, id"),
            MobileTaskFilter.Archived =>
                ("archived_at_utc IS NOT NULL", "archived_at_utc DESC, id"),
            _ => ("archived_at_utc IS NULL", "created_at_utc DESC, id"),
        };

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, title, notes, is_completed, created_at_utc, updated_at_utc, revision,
                   completed_at_utc, archived_at_utc, restored_at_utc, server_revision, device_id
            FROM tasks WHERE {where} ORDER BY {order};
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
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
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

    public async Task<int> CountTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireOwnerAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tasks;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async Task RequireOwnerAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_key FROM projection_meta WHERE singleton = 1;";
        var owner = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(owner as string, Identity.OwnerKey, StringComparison.Ordinal))
        {
            throw new ProjectionOwnershipException(Identity, owner as string);
        }
    }

    private static Dictionary<Guid, SyncedTask> AggregateTasks(
        IEnumerable<SyncedTask> tasks,
        IEnumerable<TaskTombstone> tombstones)
    {
        var final = new Dictionary<Guid, SyncedTask>();
        foreach (var task in tasks)
        {
            if (task.ServerRevision == 0)
            {
                throw new ArgumentException("Incremental tasks require a positive server revision.");
            }

            if (final.TryGetValue(task.Id, out var current))
            {
                if (task.ServerRevision < current.ServerRevision)
                {
                    continue;
                }

                if (task.ServerRevision == current.ServerRevision && task != current)
                {
                    throw new ArgumentException(
                        $"Conflicting task revisions for '{task.Id}' occurred in the same page.");
                }
            }

            final[task.Id] = task;
        }

        // Server revisions, never payload revisions or arrival order, decide the
        // final row. A delete wins an exact-revision tie. Aggregating tombstones
        // first makes the result independent of input order.
        foreach (var group in tombstones.GroupBy(t => t.Id))
        {
            var tombstone = group.OrderByDescending(t => t.ServerRevision).First();
            if (!final.TryGetValue(tombstone.Id, out var task)
                || tombstone.ServerRevision >= task.ServerRevision)
            {
                final.Remove(tombstone.Id);
            }
        }

        return final;
    }

    private static void ValidatePageTasks(IEnumerable<SyncedTask> tasks)
    {
        foreach (var task in tasks)
        {
            if (task.Id == Guid.Empty || string.IsNullOrWhiteSpace(task.Title)
                || task.Revision < 0 || task.ServerRevision < 0)
            {
                throw new ArgumentException("The page contains an invalid task.");
            }
        }
    }

    private static void ValidatePageTombstones(IEnumerable<TaskTombstone> tombstones)
    {
        foreach (var tombstone in tombstones)
        {
            if (tombstone.Id == Guid.Empty || tombstone.ServerRevision < 0)
            {
                throw new ArgumentException("The page contains an invalid tombstone.");
            }
        }
    }

    private static async Task UpsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncedTask task,
        CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT server_revision FROM task_tombstones WHERE id = $id;";
            check.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            var buried = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            // An exact-revision tombstone wins over an upsert from an earlier
            // page: only a strictly newer task row can restore a deleted key.
            if (buried is not null && buried is not DBNull
                && Convert.ToInt64(buried, CultureInfo.InvariantCulture) >= task.ServerRevision)
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
                title = excluded.title, notes = excluded.notes, is_completed = excluded.is_completed,
                created_at_utc = excluded.created_at_utc, updated_at_utc = excluded.updated_at_utc,
                revision = excluded.revision, completed_at_utc = excluded.completed_at_utc,
                archived_at_utc = excluded.archived_at_utc, restored_at_utc = excluded.restored_at_utc,
                server_revision = excluded.server_revision, device_id = excluded.device_id
            WHERE excluded.server_revision > tasks.server_revision;
            """;
        AddTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var unbury = connection.CreateCommand();
        unbury.Transaction = transaction;
        unbury.CommandText = "DELETE FROM task_tombstones WHERE id = $id AND server_revision < $serverRevision;";
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
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO task_tombstones (id, server_revision, deleted_at_utc)
            VALUES ($id, $serverRevision, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET
                server_revision = excluded.server_revision,
                deleted_at_utc = excluded.deleted_at_utc
            WHERE excluded.server_revision > task_tombstones.server_revision;
            """;
        insert.Parameters.AddWithValue("$id", tombstone.Id.ToString("D"));
        insert.Parameters.AddWithValue("$serverRevision", tombstone.ServerRevision);
        insert.Parameters.AddWithValue("$deletedAt", FormatNullable(tombstone.DeletedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = """
            DELETE FROM tasks
            WHERE id = $id AND server_revision <= $serverRevision;
            """;
        remove.Parameters.AddWithValue("$id", tombstone.Id.ToString("D"));
        remove.Parameters.AddWithValue("$serverRevision", tombstone.ServerRevision);
        await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddTaskParameters(SqliteCommand command, SyncedTask task)
    {
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
    }

    private static async Task<SyncState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cursor, baseline_snapshot_id, baseline_snapshot_server_revision,
                   baseline_snapshot_created_at, max_server_revision, last_attempt_at,
                   last_complete_sync_at, last_successful_page_at, freshness
            FROM projection_meta WHERE singleton = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Projection identity metadata is missing.");
        }

        return new SyncState(
            NullableString(reader, 0),
            NullableString(reader, 1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
            ParseFreshness(reader.GetString(8)));
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string FreshnessKey(ProjectionFreshness freshness) => freshness switch
    {
        ProjectionFreshness.Unverified => "unverified",
        ProjectionFreshness.Bootstrapping => "bootstrapping",
        ProjectionFreshness.Syncing => "syncing",
        ProjectionFreshness.Ready => "ready",
        ProjectionFreshness.Stale => "stale",
        ProjectionFreshness.NeedsRestore => "needsrestore",
        ProjectionFreshness.Failed => "failed",
        ProjectionFreshness.SnapshotTooLarge => "snapshottoo_large",
        _ => throw new ArgumentOutOfRangeException(nameof(freshness)),
    };

    private static ProjectionFreshness ParseFreshness(string value) => value switch
    {
        "unverified" => ProjectionFreshness.Unverified,
        "bootstrapping" => ProjectionFreshness.Bootstrapping,
        "syncing" => ProjectionFreshness.Syncing,
        "ready" => ProjectionFreshness.Ready,
        "stale" => ProjectionFreshness.Stale,
        "needsrestore" => ProjectionFreshness.NeedsRestore,
        "failed" => ProjectionFreshness.Failed,
        "snapshottoo_large" => ProjectionFreshness.SnapshotTooLarge,
        _ => throw new InvalidOperationException("Projection freshness metadata is invalid."),
    };

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
