using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

/// <summary>
/// Owns one verified account's rebuildable projection. Existing files are
/// opened read-only for inspection before anything is changed; no in-place
/// migration or reset is permitted.
/// </summary>
public sealed class MobileDatabase
{
    public const int CurrentSchemaVersion = ProjectionSchema.Version;

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
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task InitializeAsync(
        ProjectionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(DataSource))
        {
            await InspectExistingAsync(identity, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A sidecar without its main file is not a valid projection. Preserve it
        // for diagnosis rather than attaching it to a newly-created database.
        if (File.Exists(DataSource + "-wal") || File.Exists(DataSource + "-shm"))
        {
            throw new InvalidOperationException("The projection main database file is missing.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DataSource)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ProjectionSchema.CreateSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = """
            INSERT INTO projection_meta (singleton, owner_key, schema_version)
            VALUES (1, $owner, $version);
            """;
        owner.Parameters.AddWithValue("$owner", identity.OwnerKey);
        owner.Parameters.AddWithValue("$version", ProjectionSchema.Version);
        await owner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InspectExistingAsync(
        ProjectionIdentity identity,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ProjectionSchema.ValidateAsync(connection, identity, cancellationToken).ConfigureAwait(false);
    }
}
