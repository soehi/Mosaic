using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Storage.Sqlite.Tests;

public sealed class TransactionTests
{
    [Fact]
    public async Task Lock_timeout_returns_sanitized_failure_without_retrying_write()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var first = new SqliteModule(new(path, BusyTimeoutSeconds: 1), new Diagnostics());
        await first.ActivateAsync();
        await first.Session.ExecuteAsync(new("CREATE TABLE locked(value INTEGER)"));
        await using var second = new SqliteModule(new(path, BusyTimeoutSeconds: 1), new Diagnostics());
        await second.ActivateAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var held = first.Session.TransactionAsync(session =>
        {
            session.Execute(new("INSERT INTO locked VALUES(1)"));
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return true;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var failed = await second.Session.ExecuteAsync(new("INSERT INTO locked VALUES(2)"));
            Assert.Equal(ErrorCodes.StorageUnavailable, failed.Error?.Code);
            Assert.NotNull(failed.Error?.CorrelationId);
            Assert.DoesNotContain(path, failed.Error!.Message);
        }
        finally { release.Set(); }
        Assert.True((await held).IsSuccess);
        Assert.Single((await second.Session.QueryAsync(new("SELECT * FROM locked"))).Value);
        await first.StopAsync(); await second.StopAsync(); File.Delete(path);
    }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    [Fact]
    public async Task Migration_failure_rolls_back_and_checksum_and_newer_versions_are_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var module = new SqliteModule(new(path), new Diagnostics()); await module.ActivateAsync();
        var db = module.Session;
        var first = new MigrationDefinition(1, "CREATE TABLE records(value TEXT);");
        Assert.True((await db.MigrateAsync("example", [first])).IsSuccess);
        Assert.False((await db.MigrateAsync("example", [first with { Sql = "CREATE TABLE records(other TEXT);" }])).IsSuccess);
        Assert.False((await db.MigrateAsync("example", [])).IsSuccess);
        Assert.False((await db.MigrateAsync("example", [first, new(2, "CREATE TABLE partial(value TEXT); INVALID SQL;")])).IsSuccess);
        Assert.Empty((await db.QueryAsync(new("SELECT name FROM sqlite_master WHERE name='partial'"))).Value);
        Assert.Single((await db.QueryAsync(new("SELECT * FROM mosaic_migrations"))).Value);
        Assert.True((await db.MigrateAsync("example", [first, new(2, "ALTER TABLE records ADD COLUMN note TEXT;")])).IsSuccess);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*"));
        await module.StopAsync();
        File.Delete(path);
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*")) File.Delete(backup);
    }
    [Fact]
    public async Task Repeated_upgrades_keep_only_the_latest_five_backups()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var module = new SqliteModule(new(path), new Diagnostics());
        await module.ActivateAsync();
        var migrations = new List<MigrationDefinition> { new(1, "CREATE TABLE retained(version INTEGER);") };
        Assert.True((await module.Session.MigrateAsync("retained", migrations)).IsSuccess);
        for (var version = 2; version <= 7; version++)
        {
            migrations.Add(new(version, $"INSERT INTO retained VALUES({version});"));
            Assert.True((await module.Session.MigrateAsync("retained", migrations)).IsSuccess);
        }
        Assert.Equal(5, Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*.sqlite").Length);
        await module.StopAsync();
        File.Delete(path);
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".backup-*")) File.Delete(backup);
    }
    [Fact]
    public async Task Accepted_write_finishes_after_cancellation_and_stop_drains_queue()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var module = new SqliteModule(new(path, 1), new Diagnostics()); await module.ActivateAsync();
        await module.Session.ExecuteAsync(new("CREATE TABLE sample(value INTEGER)"));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var write = module.Session.TransactionAsync(session =>
        {
            session.Execute(new("INSERT INTO sample VALUES(1)")); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return true;
        }, cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        var read = module.Session.QueryAsync(new("SELECT * FROM sample"));
        var stop = module.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.Session.QueryAsync(new("SELECT 1"))).Error?.Code);
        release.Set();
        Assert.True((await write).IsSuccess); Assert.Single((await read).Value); await stop;
        File.Delete(path);
    }
    [Fact]
    public async Task Session_cannot_escape_callback_or_reenter_queue()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var module = new SqliteModule(new(path), new Diagnostics()); await module.ActivateAsync();
        ITransactionSession? escaped = null;
        Assert.True((await module.Session.TransactionAsync(s => { escaped = s; return true; })).IsSuccess);
        Assert.Throws<InvalidOperationException>(() => escaped!.Query(new("SELECT 1")));
        var invoked = false;
        var asynchronous = await module.Session.TransactionAsync(_ => { invoked = true; return Task.CompletedTask; });
        Assert.False(invoked);
        Assert.Equal(ErrorCodes.InvalidRequest, asynchronous.Error?.Code);
        var canceled = await module.Session.ExecuteAsync(new("CREATE TABLE canceled(value TEXT)"), new CancellationToken(true));
        Assert.Equal(ErrorCodes.Canceled, canceled.Error?.Code);
        await module.StopAsync(); File.Delete(path);
    }
}
