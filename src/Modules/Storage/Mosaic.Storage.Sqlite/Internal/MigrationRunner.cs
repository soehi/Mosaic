using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mosaic.Storage.Contracts;

namespace Mosaic.Storage.Sqlite.Internal;

internal static class MigrationRunner
{
    internal static void Apply(SqliteConnection connection, string path, string moduleId, IReadOnlyList<MigrationDefinition> migrations, int backupRetention = 5)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || migrations.Select(m => m.Version).Where((v, i) => v != i + 1).Any())
            throw new InvalidOperationException("Migrations must be contiguous from version one.");
        using var session = new SqliteSession(connection, null);
        session.Execute(new("CREATE TABLE IF NOT EXISTS mosaic_migrations(module_id TEXT NOT NULL, version INTEGER NOT NULL, digest TEXT NOT NULL, PRIMARY KEY(module_id, version));"));
        var applied = session.Query(new("SELECT version, digest FROM mosaic_migrations WHERE module_id=$module ORDER BY version", new Dictionary<string, object?> { ["$module"] = moduleId }));
        foreach (var row in applied)
        {
            var version = row.Int64("version");
            if (version < 1 || version > migrations.Count)
                throw new InvalidOperationException("Migration checksum or database version is incompatible.");
            var migration = migrations[(int)version - 1];
            var stored = row.String("digest");
            var canonical = Digest(migration.Sql);
            if (stored == canonical) continue;
            if (!MatchesLegacyDigest(stored, migration.Sql))
                throw new InvalidOperationException("Migration checksum or database version is incompatible.");
            // 0.18.4 及更早版本按嵌入资源原文取摘要，Git 检出或编辑器改写换行后同一 SQL 会得到不同摘要。
            // 旧摘要校验通过后回写为换行归一化的规范摘要，之后版本不再受工作副本换行差异影响。
            session.Execute(new("UPDATE mosaic_migrations SET digest=$digest WHERE module_id=$module AND version=$version",
                new Dictionary<string, object?> { ["$module"] = moduleId, ["$version"] = version, ["$digest"] = canonical }));
        }
        if (applied.Count == migrations.Count) return;
        var hasUserTables = session.Query(new("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> 'mosaic_migrations'")).Count > 0;
        if (applied.Count > 0 || hasUserTables)
        {
            if (backupRetention > 0)
            {
                // SQLite backup creates a consistent snapshot before any upgrade transaction.
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".sqlite", Pooling = false }.ToString());
                backup.Open();
                connection.BackupDatabase(backup);
                backup.Close();
            }
            PruneBackups(path, backupRetention);
        }
        using var transaction = connection.BeginTransaction();
        using var upgrade = new SqliteSession(connection, transaction);
        foreach (var migration in migrations.Skip(applied.Count))
        {
            upgrade.Execute(new(migration.Sql));
            upgrade.Execute(new("INSERT INTO mosaic_migrations(module_id,version,digest) VALUES($module,$version,$digest)", new Dictionary<string, object?>
            {
                ["$module"] = moduleId,
                ["$version"] = migration.Version,
                ["$digest"] = Digest(migration.Sql)
            }));
        }
        transaction.Commit();
    }
    private static void PruneBackups(string databasePath, int backupRetention)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        var prefix = Path.GetFileName(databasePath) + ".backup-";
        foreach (var stale in Directory.GetFiles(directory, prefix + "*.sqlite").OrderByDescending(path => path, StringComparer.Ordinal).Skip(Math.Max(0, backupRetention)))
        {
            try { File.Delete(stale); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal static string Normalize(string sql) => sql.Replace("\r\n", "\n").Replace('\r', '\n');
    private static string Digest(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sql))));
    private static bool MatchesLegacyDigest(string stored, string sql) =>
        stored == RawDigest(sql) || stored == RawDigest(Normalize(sql).Replace("\n", "\r\n"));
    private static string RawDigest(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
}
