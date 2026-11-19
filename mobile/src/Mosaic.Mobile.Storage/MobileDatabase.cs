using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

/// <summary>
/// Owns the mobile SQLite file. The schema is the mobile client's own; it is
/// not a copy of the desktop schema and can be dropped at any time.
///
/// <c>PRAGMA user_version</c> is the migration marker. A database written by a
/// newer app is refused instead of silently misread.
/// </summary>
public sealed class MobileDatabase
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;

    public MobileDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DataSource = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();
    }

    public string DataSource { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>Creates or migrates the schema. Safe to call on every launch.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"The local database schema {version} is newer than this build ({CurrentSchemaVersion}).");
        }

        if (version == CurrentSchemaVersion)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = version == 0 ? CreateSchemaSql : string.Empty;
        if (!string.IsNullOrEmpty(command.CommandText))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var bump = connection.CreateCommand();
        bump.Transaction = (SqliteTransaction)transaction;
        bump.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
        await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// v1 schema. <c>outbox</c> is created now but unused: write sync is phase 2,
    /// and reserving the shape keeps that migration non-destructive.
    /// </summary>
    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tasks (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            notes TEXT NOT NULL DEFAULT '',
            is_completed INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            revision INTEGER NOT NULL,
            completed_at_utc TEXT NULL,
            archived_at_utc TEXT NULL,
            restored_at_utc TEXT NULL,
            server_revision INTEGER NOT NULL,
            device_id TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_tasks_archived ON tasks (archived_at_utc);
        CREATE INDEX IF NOT EXISTS ix_tasks_completed ON tasks (is_completed);

        CREATE TABLE IF NOT EXISTS task_tombstones (
            id TEXT PRIMARY KEY,
            server_revision INTEGER NOT NULL,
            deleted_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value_json TEXT NOT NULL,
            server_revision INTEGER NOT NULL,
            updated_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS outbox (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            item_key TEXT NOT NULL,
            operation TEXT NOT NULL,
            payload_json TEXT NULL,
            base_server_revision INTEGER NOT NULL,
            client_revision INTEGER NULL,
            idempotency_key TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_outbox_state ON outbox (state, created_at_utc);
        """;
}
