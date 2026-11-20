using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Storage.Sqlite.Tests;

public sealed class StorageRelocationTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }

    [Fact]
    public async Task Plan_accepts_existing_target_and_reports_paths()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);

        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory));

        Assert.True(plan.IsSuccess);
        Assert.Equal(layout.DatabasePath, plan.Value.SourceDatabasePath);
        Assert.Equal(Path.Combine(layout.TargetDirectory, "mosaic.sqlite"), plan.Value.TargetDatabasePath);
        Assert.True(plan.Value.SourceBytes > 0);
        Assert.True(plan.Value.KeepSource);
    }

    [Fact]
    public async Task Plan_rejects_missing_target_directory()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);

        var plan = await relocation.PlanAsync(new StorageRelocationRequest(Path.Combine(layout.Root, "missing")));

        Assert.False(plan.IsSuccess);
        Assert.Equal("target", plan.Error?.Field);
    }

    [Fact]
    public async Task Plan_rejects_current_directory()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);

        var plan = await relocation.PlanAsync(new StorageRelocationRequest(Path.GetDirectoryName(layout.DatabasePath)!));

        Assert.False(plan.IsSuccess);
        Assert.Equal("target", plan.Error?.Field);
    }

    [Fact]
    public async Task Plan_rejects_target_that_already_holds_a_database()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        File.WriteAllText(Path.Combine(layout.TargetDirectory, "mosaic.sqlite"), "occupied");
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);

        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory));

        Assert.False(plan.IsSuccess);
        Assert.Equal("target", plan.Error?.Field);
    }

    [Fact]
    public async Task Plan_rejects_relative_target()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);

        var plan = await relocation.PlanAsync(new StorageRelocationRequest("relative-folder"));

        Assert.False(plan.IsSuccess);
        Assert.Equal("target", plan.Error?.Field);
    }

    [Fact]
    public async Task Execute_copies_rows_and_keeps_source_when_requested()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);
        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory));

        var executed = await SqliteStorageRelocation.ExecuteAsync(plan.Value);

        Assert.True(executed.IsSuccess);
        Assert.True(File.Exists(layout.DatabasePath));
        Assert.Equal("迁移样例", await ReadSampleAsync(plan.Value.TargetDatabasePath));
    }

    [Fact]
    public async Task Execute_removes_source_when_keep_source_is_false()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);
        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory, KeepSource: false));

        var executed = await SqliteStorageRelocation.ExecuteAsync(plan.Value);

        Assert.True(executed.IsSuccess);
        Assert.False(File.Exists(layout.DatabasePath));
        Assert.Equal("迁移样例", await ReadSampleAsync(plan.Value.TargetDatabasePath));
    }

    [Fact]
    public async Task Execute_refuses_to_overwrite_an_existing_target_database()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);
        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory));
        await File.WriteAllTextAsync(plan.Value.TargetDatabasePath, "occupied");

        var executed = await SqliteStorageRelocation.ExecuteAsync(plan.Value);

        Assert.False(executed.IsSuccess);
        Assert.True(File.Exists(layout.DatabasePath));
        Assert.Equal("occupied", await File.ReadAllTextAsync(plan.Value.TargetDatabasePath));
    }

    [Fact]
    public async Task Request_file_round_trips_and_can_be_deleted()
    {
        using var layout = new TestLayout();
        await layout.CreateDatabaseAsync();
        var relocation = new SqliteStorageRelocation(layout.DatabasePath, layout.ConfigDirectory);
        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory, KeepSource: false));

        Assert.True((await relocation.ScheduleAsync(plan.Value)).IsSuccess);
        var request = await SqliteStorageRelocation.ReadRequestAsync(layout.ConfigDirectory);

        Assert.NotNull(request);
        Assert.Equal(layout.TargetDirectory, request!.TargetDirectory);
        Assert.False(request.KeepSource);

        SqliteStorageRelocation.DeleteRequest(layout.ConfigDirectory);
        Assert.Null(await SqliteStorageRelocation.ReadRequestAsync(layout.ConfigDirectory));
    }

    [Fact]
    public async Task Unreadable_request_file_is_treated_as_absent()
    {
        using var layout = new TestLayout();
        Directory.CreateDirectory(layout.ConfigDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(layout.ConfigDirectory, StorageRelocationFiles.RequestFileName),
            "{ not json");

        Assert.Null(await SqliteStorageRelocation.ReadRequestAsync(layout.ConfigDirectory));
    }

    private static async Task<string> ReadSampleAsync(string databasePath)
    {
        await using var module = new SqliteModule(new(databasePath), new Diagnostics());
        await module.ActivateAsync();
        try
        {
            var rows = await module.Session.QueryAsync(new("SELECT value FROM sample"));
            return Assert.Single(rows.Value).String("value");
        }
        finally { await module.StopAsync(); }
    }

    private sealed class TestLayout : IDisposable
    {
        public TestLayout()
        {
            Root = Path.Combine(Path.GetTempPath(), "Mosaic-relocation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "Data"));
            Directory.CreateDirectory(TargetDirectory);
        }

        public string Root { get; }
        public string DatabasePath => Path.Combine(Root, "Data", "mosaic.sqlite");
        public string TargetDirectory => Path.Combine(Root, "Target");
        public string ConfigDirectory => Path.Combine(Root, "Config");

        public async Task CreateDatabaseAsync()
        {
            await using var module = new SqliteModule(new(DatabasePath), new Diagnostics());
            await module.ActivateAsync();
            Assert.True((await module.Session.ExecuteAsync(new("CREATE TABLE sample(value TEXT NOT NULL)"))).IsSuccess);
            Assert.True((await module.Session.ExecuteAsync(
                new("INSERT INTO sample VALUES($value)", new Dictionary<string, object?> { ["$value"] = "迁移样例" }))).IsSuccess);
            await module.StopAsync();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
