using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

internal static class ProjectionSchema
{
    public const int Version = 2;

    public const string CreateSql = """
        PRAGMA user_version = 2;

        CREATE TABLE projection_meta (
            singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
            owner_key TEXT NOT NULL,
            schema_version INTEGER NOT NULL CHECK (schema_version = 2),
            cursor TEXT NULL,
            baseline_snapshot_id TEXT NULL,
            baseline_snapshot_server_revision INTEGER NULL,
            baseline_snapshot_created_at TEXT NULL,
            max_server_revision INTEGER NOT NULL DEFAULT 0 CHECK (max_server_revision >= 0),
            last_attempt_at TEXT NULL,
            last_complete_sync_at TEXT NULL,
            last_successful_page_at TEXT NULL,
            freshness TEXT NOT NULL DEFAULT 'unverified'
                CHECK (freshness IN ('unverified','bootstrapping','syncing','ready','stale','needsrestore','failed','snapshottoo_large'))
        );

        CREATE TABLE tasks (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            notes TEXT NOT NULL DEFAULT '',
            is_completed INTEGER NOT NULL DEFAULT 0 CHECK (is_completed IN (0, 1)),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            completed_at_utc TEXT NULL,
            archived_at_utc TEXT NULL,
            restored_at_utc TEXT NULL,
            server_revision INTEGER NOT NULL CHECK (server_revision >= 0),
            device_id TEXT NULL
        );

        CREATE INDEX ix_tasks_all ON tasks (created_at_utc DESC, id);
        CREATE INDEX ix_tasks_open ON tasks (is_completed, created_at_utc DESC, id)
            WHERE archived_at_utc IS NULL AND is_completed = 0;
        CREATE INDEX ix_tasks_completed ON tasks (completed_at_utc DESC, id)
            WHERE archived_at_utc IS NULL AND is_completed = 1;
        CREATE INDEX ix_tasks_archived ON tasks (archived_at_utc DESC, id)
            WHERE archived_at_utc IS NOT NULL;

        CREATE TABLE task_tombstones (
            id TEXT PRIMARY KEY,
            server_revision INTEGER NOT NULL CHECK (server_revision >= 0),
            deleted_at_utc TEXT NULL
        );
        """;

    private static readonly Dictionary<string, string[]> RequiredColumns = new()
    {
        ["projection_meta"] =
        [
            "singleton", "owner_key", "schema_version", "cursor", "baseline_snapshot_id",
            "baseline_snapshot_server_revision", "baseline_snapshot_created_at", "max_server_revision",
            "last_attempt_at", "last_complete_sync_at", "last_successful_page_at", "freshness",
        ],
        ["tasks"] =
        [
            "id", "title", "notes", "is_completed", "created_at_utc", "updated_at_utc", "revision",
            "completed_at_utc", "archived_at_utc", "restored_at_utc", "server_revision", "device_id",
        ],
        ["task_tombstones"] = ["id", "server_revision", "deleted_at_utc"],
    };

    private static readonly string[] RequiredIndexes =
        ["ix_tasks_all", "ix_tasks_open", "ix_tasks_completed", "ix_tasks_archived"];

    public static async Task ValidateAsync(
        SqliteConnection connection,
        ProjectionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var actualVersion = Convert.ToInt32(
            await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (actualVersion != Version)
        {
            throw new InvalidOperationException(
                $"Projection schema {actualVersion} is incompatible with version {Version}.");
        }

        foreach (var (table, columns) in RequiredColumns)
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            exists.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
            {
                throw new InvalidOperationException($"Projection table '{table}' is missing.");
            }

            await using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actual.Add(reader.GetString(1));
            }

            if (!columns.All(actual.Contains))
            {
                throw new InvalidOperationException($"Projection table '{table}' has an incompatible schema.");
            }
        }

        await using var indexes = connection.CreateCommand();
        indexes.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_tasks_%';";
        await using var indexReader = await indexes.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actualIndexes = new HashSet<string>(StringComparer.Ordinal);
        while (await indexReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actualIndexes.Add(indexReader.GetString(0));
        }

        if (!RequiredIndexes.All(actualIndexes.Contains))
        {
            throw new InvalidOperationException("Projection task indexes are incomplete.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_key, schema_version
            FROM projection_meta WHERE singleton = 1;
            """;
        await using var meta = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await meta.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Projection identity metadata is missing.");
        }

        var owner = meta.GetString(0);
        if (!string.Equals(owner, identity.OwnerKey, StringComparison.Ordinal))
        {
            throw new ProjectionOwnershipException(identity, owner);
        }

        if (meta.GetInt64(1) != Version)
        {
            throw new InvalidOperationException("Projection schema metadata is inconsistent.");
        }
    }
}

public sealed class ProjectionOwnershipException(
    ProjectionIdentity expected,
    string? actualOwnerKey) : InvalidOperationException(
    "The projection belongs to a different account or server and was not opened.")
{
    public ProjectionIdentity ExpectedIdentity { get; } = expected;

    public string? ActualOwnerKey { get; } = actualOwnerKey;
}
