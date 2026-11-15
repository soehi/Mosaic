using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Storage.Sqlite.Tests;

public sealed class DatabaseTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    private sealed class ThrowingDiagnostics : IDiagnosticSink { public void Report(string id, Exception ex) => throw new IOException("diagnostics unavailable"); }
    [Fact]
    public async Task Public_protocol_persists_on_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using (var module = new SqliteModule(new(path), new Diagnostics()))
            {
                await module.ActivateAsync();
                Assert.True((await module.Session.ExecuteAsync(new("CREATE TABLE sample(value TEXT NOT NULL)"))).IsSuccess);
                Assert.True((await module.Session.ExecuteAsync(new("INSERT INTO sample VALUES($value)", new Dictionary<string, object?> { ["$value"] = "中文 ' ;" }))).IsSuccess);
            }
            await using var reopened = new SqliteModule(new(path), new Diagnostics());
            await reopened.ActivateAsync();
            var rows = await reopened.Session.QueryAsync(new("SELECT value FROM sample"));
            Assert.Equal("中文 ' ;", Assert.Single(rows.Value).String("value"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Duplicate_column_names_remain_available_by_ordinal()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var module = new SqliteModule(new(path), new Diagnostics());
            await module.ActivateAsync();
            await module.Session.ExecuteAsync(new("CREATE TABLE left_side(value TEXT)"));
            await module.Session.ExecuteAsync(new("CREATE TABLE right_side(value TEXT)"));
            await module.Session.ExecuteAsync(new("INSERT INTO left_side VALUES('left')"));
            await module.Session.ExecuteAsync(new("INSERT INTO right_side VALUES('right')"));
            var row = Assert.Single((await module.Session.QueryAsync(new("SELECT left_side.value, right_side.value FROM left_side CROSS JOIN right_side"))).Value);
            Assert.Equal(2, row.ColumnCount);
            Assert.Equal(new[] { "value", "value" }, row.Names);
            Assert.Equal("left", row.Value(0));
            Assert.Equal("right", row.Value(1));
            Assert.Equal("left", row.String("value"));
            await module.StopAsync();
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Diagnostics_failure_does_not_stop_the_storage_queue()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var module = new SqliteModule(new(path), new ThrowingDiagnostics());
            await module.ActivateAsync();
            var failed = await module.Session.QueryAsync(new("SELECT * FROM missing_table"));
            Assert.Equal(ErrorCodes.StorageUnavailable, failed.Error?.Code);
            Assert.NotNull(failed.Error?.CorrelationId);
            Assert.True((await module.Session.ExecuteAsync(new("CREATE TABLE recovered(value INTEGER)"))).IsSuccess);
            await module.StopAsync();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Journal_mode_wal_round_trips_with_delete_and_preserves_data()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using (var module = new SqliteModule(new(path, JournalMode: SqliteJournalMode.Delete), new Diagnostics()))
            {
                await module.ActivateAsync();
                Assert.Equal("delete", JournalMode(module));
                await module.Session.ExecuteAsync(new("CREATE TABLE sample(value TEXT NOT NULL)"));
                await module.Session.ExecuteAsync(new("INSERT INTO sample VALUES($value)", new Dictionary<string, object?> { ["$value"] = "first" }));
            }

            await using (var wal = new SqliteModule(new(path, JournalMode: SqliteJournalMode.Wal), new Diagnostics()))
            {
                await wal.ActivateAsync();
                Assert.Equal("wal", JournalMode(wal));
                await wal.Session.ExecuteAsync(new("INSERT INTO sample VALUES($value)", new Dictionary<string, object?> { ["$value"] = "second" }));
                Assert.True(File.Exists(path + "-wal") || File.Exists(path + "-shm"));
            }

            await using (var reopened = new SqliteModule(new(path, JournalMode: SqliteJournalMode.Delete), new Diagnostics()))
            {
                await reopened.ActivateAsync();
                Assert.Equal("delete", JournalMode(reopened));
                var rows = await reopened.Session.QueryAsync(new("SELECT value FROM sample ORDER BY value"));
                Assert.Equal(new[] { "first", "second" }, rows.Value.Select(row => row.String("value")));
            }
        }
        finally
        {
            File.Delete(path);
            foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }
    }

    [Fact]
    public async Task Pooled_per_operation_mode_serializes_operations_and_preserves_data()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var module = new SqliteModule(new(path, ConnectionMode: SqliteConnectionMode.PooledPerOperation), new Diagnostics());
            await module.ActivateAsync();
            await module.Session.ExecuteAsync(new("CREATE TABLE counter(value INTEGER NOT NULL)"));
            await module.Session.ExecuteAsync(new("INSERT INTO counter VALUES(0)"));

            var increments = Enumerable.Range(0, 40).Select(_ => module.Session.TransactionAsync(session =>
            {
                var row = Assert.Single(session.Query(new("SELECT value FROM counter")));
                session.Execute(new("UPDATE counter SET value=$value", new Dictionary<string, object?> { ["$value"] = row.Int64("value") + 1 }));
                return true;
            }));
            await Task.WhenAll(increments);

            var result = await module.Session.QueryAsync(new("SELECT value FROM counter"));
            Assert.Equal(40, Assert.Single(result.Value).Int64("value"));
            await module.StopAsync();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Backup_retention_setting_controls_backup_pruning()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        var directory = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + ".backup-";
        try
        {
            await using (var module = new SqliteModule(new(path, BackupRetention: 2), new Diagnostics()))
            {
                await module.ActivateAsync();
                var db = module.Session;
                await db.ExecuteAsync(new("CREATE TABLE records(value TEXT)"));
                await db.MigrateAsync("example", [new MigrationDefinition(1, "CREATE TABLE v1(value TEXT);")]);
                await db.MigrateAsync("example", [new MigrationDefinition(1, "CREATE TABLE v1(value TEXT);"), new(2, "CREATE TABLE v2(value TEXT);")]);
                await db.MigrateAsync("example", [new MigrationDefinition(1, "CREATE TABLE v1(value TEXT);"), new(2, "CREATE TABLE v2(value TEXT);"), new(3, "CREATE TABLE v3(value TEXT);")]);
                await module.StopAsync();
                Assert.Equal(2, Directory.GetFiles(directory, prefix + "*.sqlite").Length);
            }

            await using (var module = new SqliteModule(new(path, BackupRetention: 0), new Diagnostics()))
            {
                await module.ActivateAsync();
                await module.Session.MigrateAsync("example", [new MigrationDefinition(1, "CREATE TABLE v1(value TEXT);"), new(2, "CREATE TABLE v2(value TEXT);"), new(3, "CREATE TABLE v3(value TEXT);"), new(4, "CREATE TABLE v4(value TEXT);")]);
                await module.StopAsync();
                Assert.Empty(Directory.GetFiles(directory, prefix + "*.sqlite"));
            }
        }
        finally
        {
            foreach (var backup in Directory.GetFiles(directory, prefix + "*.sqlite")) File.Delete(backup);
            File.Delete(path);
        }
    }

    [Fact]
    public void Storage_setting_source_defines_the_five_storage_parameters()
    {
        var source = new StorageSettingSource();
        Assert.Equal("storage-sqlite", source.ModuleId);
        Assert.Equal(5, source.Definitions.Count);
        Assert.All(source.Definitions, definition => Assert.Equal("storage-sqlite", definition.ModuleId));
        Assert.All(source.Definitions, definition => Assert.Equal(SettingPersistence.HostConfig, definition.Persistence));
        Assert.Equal(SettingApplyMode.Restart, source.Definitions.Single(definition => definition.Id == "storage.journalMode").ApplyMode);
        Assert.Equal(new[] { "Delete", "Wal" }, source.Definitions.Single(definition => definition.Id == "storage.journalMode").AllowedValues);
        Assert.Equal(128, source.Definitions.Single(definition => definition.Id == "storage.queueCapacity").DefaultValue);
        Assert.Equal(16, source.Definitions.Single(definition => definition.Id == "storage.queueCapacity").Min);
        Assert.Equal(4096, source.Definitions.Single(definition => definition.Id == "storage.queueCapacity").Max);
        Assert.Equal(5, source.Definitions.Single(definition => definition.Id == "storage.backupRetention").DefaultValue);
        Assert.Equal(0, source.Definitions.Single(definition => definition.Id == "storage.backupRetention").Min);
        Assert.Equal("ReusedSingle", source.Definitions.Single(definition => definition.Id == "storage.connectionMode").DefaultValue);
    }

    private static string JournalMode(SqliteModule module)
    {
        var result = module.Session.QueryAsync(new("PRAGMA journal_mode")).GetAwaiter().GetResult();
        return Assert.Single(result.Value).Value(0)!.ToString()!;
    }
}
