using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Storage.Sqlite.Tests;

// 0.18.4 升级失败的回归测试：同一迁移 SQL 因工作副本换行不同得到不同摘要，
// 旧库记录的 LF/CRLF 摘要都必须被接受，并回写为换行归一化的规范摘要。
public sealed class MigrationDigestCompatibilityTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }

    private const string LfSql = "CREATE TABLE legacy(value TEXT);\nINSERT INTO legacy VALUES('a');\n";
    private static readonly string CrlfSql = LfSql.Replace("\n", "\r\n");

    [Fact]
    public async Task Legacy_crlf_digest_is_accepted_by_lf_resource_and_rewritten_to_canonical()
    {
        var path = NewPath();
        try
        {
            await SeedAsync(path, CrlfSql, RawDigest(CrlfSql));
            await using var module = new SqliteModule(new(path), new Diagnostics());
            await module.ActivateAsync();

            var result = await module.Session.MigrateAsync("legacy", [new(1, LfSql)]);

            Assert.True(result.IsSuccess);
            Assert.Equal(Canonical(LfSql), await StoredDigestAsync(module, "legacy"));
            Assert.Single((await module.Session.QueryAsync(new("SELECT * FROM legacy"))).Value);
            await module.StopAsync();
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Legacy_lf_digest_is_accepted_by_crlf_resource_and_rewritten_to_canonical()
    {
        var path = NewPath();
        try
        {
            await SeedAsync(path, LfSql, RawDigest(LfSql));
            await using var module = new SqliteModule(new(path), new Diagnostics());
            await module.ActivateAsync();

            var result = await module.Session.MigrateAsync("legacy", [new(1, CrlfSql)]);

            Assert.True(result.IsSuccess);
            Assert.Equal(Canonical(LfSql), await StoredDigestAsync(module, "legacy"));
            await module.StopAsync();
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Rewritten_digest_stays_valid_across_later_line_ending_switches()
    {
        var path = NewPath();
        try
        {
            await SeedAsync(path, CrlfSql, RawDigest(CrlfSql));
            await using var module = new SqliteModule(new(path), new Diagnostics());
            await module.ActivateAsync();
            Assert.True((await module.Session.MigrateAsync("legacy", [new(1, LfSql)])).IsSuccess);

            // 同一迁移再次以 CRLF、LF 两种原文检查都应通过。
            Assert.True((await module.Session.MigrateAsync("legacy", [new(1, CrlfSql)])).IsSuccess);
            Assert.True((await module.Session.MigrateAsync("legacy", [new(1, LfSql)])).IsSuccess);
            await module.StopAsync();
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Changed_migration_sql_is_still_rejected()
    {
        var path = NewPath();
        try
        {
            await SeedAsync(path, LfSql, RawDigest(LfSql));
            await using var module = new SqliteModule(new(path), new Diagnostics());
            await module.ActivateAsync();

            var result = await module.Session.MigrateAsync("legacy", [new(1, LfSql.Replace("value TEXT", "other TEXT"))]);

            Assert.False(result.IsSuccess);
            await module.StopAsync();
        }
        finally { DeleteDatabase(path); }
    }

    private static async Task SeedAsync(string path, string sql, string digest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS mosaic_migrations(module_id TEXT NOT NULL, version INTEGER NOT NULL, digest TEXT NOT NULL, PRIMARY KEY(module_id, version));");
        await ExecuteAsync(connection, sql);
        await ExecuteAsync(connection,
            "INSERT INTO mosaic_migrations(module_id,version,digest) VALUES('legacy',1,$digest);",
            ("$digest", digest));
        SqliteConnection.ClearAllPools();
    }

    private static async Task<string> StoredDigestAsync(SqliteModule module, string moduleId)
    {
        var rows = await module.Session.QueryAsync(new(
            "SELECT digest FROM mosaic_migrations WHERE module_id=$module",
            new Dictionary<string, object?> { ["$module"] = moduleId }));
        return Assert.Single(rows.Value).String("digest");
    }

    private static string Canonical(string sql) => RawDigest(sql.Replace("\r\n", "\n").Replace('\r', '\n'));

    private static string RawDigest(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*.sqlite"))
            File.Delete(backup);
    }
}