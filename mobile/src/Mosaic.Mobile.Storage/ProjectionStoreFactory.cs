using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

/// <summary>
/// Opens the single projection belonging to a verified account. The v1 cache
/// is never upgraded in place: it is validated, archived by the migration
/// stage and only then does a fresh v2 projection get created.
/// </summary>
public static class ProjectionStoreFactory
{
    public static async Task<SqliteSyncStore> OpenOrCreateAsync(
        ProjectionIdentity identity,
        string appDataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        var migration = await ProjectionMigration.MigrateLegacyV1Async(
            identity, appDataDirectory, cancellationToken).ConfigureAwait(false);
        if (migration.Outcome == ProjectionMigrationOutcome.Rejected)
        {
            throw new InvalidOperationException(migration.Reason);
        }

        var directory = Path.Combine(appDataDirectory, "projections");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, identity.OwnerKey + ".db");
        var database = new MobileDatabase(path);
        try
        {
            await database.InitializeAsync(identity, cancellationToken).ConfigureAwait(false);
            return new SqliteSyncStore(database, identity);
        }
        catch (ProjectionOwnershipException)
        {
            await QuarantineAsync(path, directory, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            SqliteConnection.ClearAllPools();
            throw;
        }
    }

    private static Task QuarantineAsync(
        string path,
        string directory,
        CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        cancellationToken.ThrowIfCancellationRequested();
        var quarantine = Path.Combine(directory, "quarantine");
        Directory.CreateDirectory(quarantine);
        var target = Path.Combine(quarantine, Path.GetFileNameWithoutExtension(path)
            + "-" + Guid.NewGuid().ToString("N") + ".db");
        var moved = new List<(string Source, string Target)>();
        try
        {
            foreach (var suffix in new[] { "-wal", "-shm", string.Empty })
            {
                var source = path + suffix;
                if (!File.Exists(source))
                {
                    continue;
                }

                var destination = target + suffix;
                File.Move(source, destination);
                moved.Add((source, destination));
            }
        }
        catch
        {
            foreach (var (source, destination) in moved.AsEnumerable().Reverse())
            {
                if (!File.Exists(source) && File.Exists(destination))
                {
                    File.Move(destination, source);
                }
            }

            throw;
        }

        return Task.CompletedTask;
    }
}
