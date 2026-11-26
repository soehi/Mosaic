using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mosaic.Mobile.Storage;

public enum ProjectionMigrationOutcome
{
    NoLegacyDatabase,
    Archived,
    AlreadyArchived,
    Rejected,
}

public sealed record ProjectionMigrationResult(
    ProjectionMigrationOutcome Outcome,
    string? ArchiveDirectory = null,
    string? Reason = null);

/// <summary>
/// Isolates the v1 cache as a complete file group. The caller must verify a
/// server and user identity before invoking migration; no legacy task, cursor,
/// setting or outbox is inherited by v2.
/// </summary>
public static class ProjectionMigration
{
    private const string LegacyFileName = "mosaic-mobile.db";
    private const string MarkerFileName = "migration.json";
    private const string PendingFileName = "migration.pending.json";
    private static readonly string[] RequiredTables = ["meta", "tasks", "task_tombstones", "settings", "outbox"];

    public static async Task<ProjectionMigrationResult> MigrateLegacyV1Async(
        ProjectionIdentity identity,
        string appDataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        if (string.IsNullOrWhiteSpace(identity.UserId))
        {
            throw new ArgumentException("A verified user id is required before migration.", nameof(identity));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.Combine(appDataDirectory, "legacy");
        Directory.CreateDirectory(root);
        var markerPath = Path.Combine(root, MarkerFileName);
        if (File.Exists(markerPath))
        {
            return new ProjectionMigrationResult(ProjectionMigrationOutcome.AlreadyArchived);
        }

        var pendingPath = Path.Combine(root, PendingFileName);
        if (File.Exists(pendingPath))
        {
            return await ResumeArchiveAsync(root, pendingPath, markerPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var sourcePath = Path.Combine(appDataDirectory, LegacyFileName);
        if (!File.Exists(sourcePath))
        {
            return new ProjectionMigrationResult(ProjectionMigrationOutcome.NoLegacyDatabase);
        }

        string? failure;
        try
        {
            failure = await ValidateLegacyAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.Message;
        }

        if (failure is not null)
        {
            return new ProjectionMigrationResult(ProjectionMigrationOutcome.Rejected, Reason: failure);
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var archive = Path.Combine(root, "v1-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
        await WritePendingAtomicAsync(pendingPath, archive, cancellationToken).ConfigureAwait(false);
        return await ResumeArchiveAsync(root, pendingPath, markerPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ProjectionMigrationResult> ResumeArchiveAsync(
        string root,
        string pendingPath,
        string markerPath,
        CancellationToken cancellationToken)
    {
        PendingMigration? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingMigration>(
                await File.ReadAllTextAsync(pendingPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A corrupt pending record cannot name a safe archive target. Keep
            // the legacy files in place and report the failure instead of
            // guessing at paths from untrusted content.
            return new ProjectionMigrationResult(
                ProjectionMigrationOutcome.Rejected,
                Reason: "The pending legacy migration record is unreadable; the original files were preserved. "
                    + exception.Message);
        }

        if (pending is null || string.IsNullOrWhiteSpace(pending.ArchiveDirectory))
        {
            return new ProjectionMigrationResult(
                ProjectionMigrationOutcome.Rejected,
                Reason: "The pending legacy migration record is invalid; the original files were preserved.");
        }

        if (!Path.GetFullPath(pending.ArchiveDirectory).StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectionMigrationResult(
                ProjectionMigrationOutcome.Rejected,
                Reason: "The pending archive path is outside the legacy directory.");
        }

        try
        {
            MoveFileGroup(Path.Combine(Path.GetDirectoryName(root)!, LegacyFileName),
                pending.ArchiveDirectory, cancellationToken);
            await WriteMarkerAtomicAsync(markerPath, pending.ArchiveDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProjectionMigrationResult(
                ProjectionMigrationOutcome.Rejected,
                Reason: "Archiving the legacy cache failed; the original files were preserved. "
                    + exception.Message);
        }

        File.Delete(pendingPath);
        return new ProjectionMigrationResult(ProjectionMigrationOutcome.Archived, pending.ArchiveDirectory);
    }

    private static async Task<string?> ValidateLegacyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // No pooled or caller-owned connection may remain when the complete file
        // group is moved. Read-only inspection leaves WAL contents untouched.
        SqliteConnection.ClearAllPools();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            var value = Convert.ToInt32(
                await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (value != 1)
            {
                return $"Legacy projection version {value} is not supported; the original file was preserved.";
            }

            foreach (var table in RequiredTables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
                command.Parameters.AddWithValue("$name", table);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                {
                    return $"Legacy table '{table}' is missing; the original file was preserved.";
                }
            }

            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase)
                ? null
                : "Legacy integrity check failed; the original file was preserved.";
        }
    }

    internal static void MoveFileGroup(
        string sourcePath,
        string archiveDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(archiveDirectory);
        var moved = new List<(string Source, string Target)>();
        try
        {
            foreach (var suffix in new[] { "-wal", "-shm", string.Empty })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sourcePath + suffix;
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.Combine(archiveDirectory, LegacyFileName + suffix);
                if (File.Exists(target))
                {
                    var sourceLength = new FileInfo(source).Length;
                    var targetLength = new FileInfo(target).Length;
                    if (sourceLength == targetLength)
                    {
                        continue;
                    }

                    throw new IOException($"Archive target has a different size: {target}");
                }

                File.Move(source, target);
                moved.Add((source, target));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Restore the complete source group when the platform cannot complete
            // the move. A later start can then resume from the pending marker.
            foreach (var (source, target) in moved.AsEnumerable().Reverse())
            {
                if (!File.Exists(source) && File.Exists(target))
                {
                    File.Move(target, source);
                }
            }

            throw;
        }
    }

    private static async Task WritePendingAtomicAsync(
        string path,
        string archiveDirectory,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(new PendingMigration(archiveDirectory, DateTimeOffset.UtcNow)),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path);
    }

    private static async Task WriteMarkerAtomicAsync(
        string path,
        string archiveDirectory,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            archivedAtUtc = DateTimeOffset.UtcNow,
            archiveDirectory,
        }), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The pending record is written before the file group moves. Both JSON names
    /// are pinned to the camelCase spelling the earlier build wrote, and the
    /// serializer's default matching is case-sensitive: a PascalCase-only record
    /// reads a camelCase file back as a null directory, which turns every
    /// migration into a rejection.
    /// </summary>
    private sealed record PendingMigration(
        [property: JsonPropertyName("archiveDirectory")] string ArchiveDirectory,
        [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc);
}
