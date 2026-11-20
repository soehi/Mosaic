using System.Text.Json;
using Mosaic.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Desktop.SmokeTests;

public sealed class StartupStorageRelocationTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string correlationId, Exception exception) { } }

    [Fact]
    public async Task Queued_relocation_moves_the_database_and_persists_the_new_location()
    {
        using var layout = new Layout();
        await layout.SeedAsync("迁移前数据");
        Assert.True(await layout.ScheduleAsync());

        var outcome = await layout.RunStartupAsync();

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("迁移前数据", await layout.ReadTargetAsync());
        Assert.True(File.Exists(layout.SourceDatabase));
        Assert.Equal(layout.TargetDirectory, layout.PersistedLocation());
        Assert.False(File.Exists(Path.Combine(layout.ConfigDirectory, StorageRelocationFiles.RequestFileName)));
    }

    [Fact]
    public async Task Queued_relocation_with_keep_source_false_removes_the_old_database()
    {
        using var layout = new Layout();
        await layout.SeedAsync("只保留新位置");
        Assert.True(await layout.ScheduleAsync(keepSource: false));

        var outcome = await layout.RunStartupAsync();

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("只保留新位置", await layout.ReadTargetAsync());
        Assert.False(File.Exists(layout.SourceDatabase));
        Assert.Equal(layout.TargetDirectory, layout.PersistedLocation());
    }

    [Fact]
    public async Task Startup_recovers_when_the_move_succeeded_but_persistence_did_not()
    {
        // Crash window: the copy is already in place, but storage.location was never written.
        using var layout = new Layout();
        await layout.SeedAsync("已搬迁");
        var relocation = new SqliteStorageRelocation(layout.SourceDatabase, layout.ConfigDirectory);
        var plan = await relocation.PlanAsync(new StorageRelocationRequest(layout.TargetDirectory, KeepSource: false));
        Assert.True(plan.IsSuccess);
        Assert.True((await SqliteStorageRelocation.ExecuteAsync(plan.Value)).IsSuccess);
        Assert.True((await relocation.ScheduleAsync(plan.Value)).IsSuccess);

        var outcome = await layout.RunStartupAsync();

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(layout.TargetDirectory, layout.PersistedLocation());
        Assert.Equal("已搬迁", await layout.ReadTargetAsync());
    }

    [Fact]
    public async Task Failed_relocation_keeps_the_source_and_reports_the_failure()
    {
        using var layout = new Layout();
        await layout.SeedAsync("源数据仍在");
        await File.WriteAllTextAsync(Path.Combine(layout.TargetDirectory, "mosaic.sqlite"), "occupied");
        // A request is accepted without validation; the startup plan then rejects the occupied target.
        Assert.True((await new SqliteStorageRelocation(layout.SourceDatabase, layout.ConfigDirectory)
            .ScheduleAsync(new StorageRelocationPlan(
                layout.SourceDatabase, layout.TargetDirectory,
                Path.Combine(layout.TargetDirectory, "mosaic.sqlite"), 0, true))).IsSuccess);

        var outcome = await layout.RunStartupAsync();

        Assert.False(outcome.Success);
        Assert.Equal("源数据仍在", await layout.ReadAsync(layout.SourceDatabase));
        Assert.Equal("local-app-data", layout.PersistedLocation());
        Assert.False(File.Exists(Path.Combine(layout.ConfigDirectory, StorageRelocationFiles.RequestFileName)));
    }

    private sealed record StartupOutcome(bool Success, string Message);

    private sealed class Layout : IDisposable
    {
        public Layout()
        {
            Root = Path.Combine(Path.GetTempPath(), "Mosaic-startup-relocation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(TargetDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(SourceDatabase)!);
            File.Copy(
                Path.Combine(RepositoryRoot(), "src/Mosaic.Desktop/appsettings.json"),
                Path.GetFullPath(Paths.ConfigPath));
        }

        private string Root { get; }
        public AppPaths Paths => new(Root);
        public string ConfigDirectory => Path.Combine(Root, "Config");
        public string TargetDirectory => Path.Combine(Root, "Moved");
        public string SourceDatabase => Path.Combine(Root, "Data", "mosaic.sqlite");
        private string TargetDatabase => Path.Combine(TargetDirectory, "mosaic.sqlite");

        public async Task SeedAsync(string value)
        {
            await using var module = new SqliteModule(new(SourceDatabase), new Diagnostics());
            await module.ActivateAsync();
            Assert.True((await module.Session.ExecuteAsync(new("CREATE TABLE sample(value TEXT NOT NULL)"))).IsSuccess);
            Assert.True((await module.Session.ExecuteAsync(new(
                "INSERT INTO sample VALUES($value)",
                new Dictionary<string, object?> { ["$value"] = value }))).IsSuccess);
            await module.StopAsync();
        }

        public async Task<bool> ScheduleAsync(bool keepSource = true)
        {
            var relocation = new SqliteStorageRelocation(SourceDatabase, ConfigDirectory);
            var plan = await relocation.PlanAsync(new StorageRelocationRequest(TargetDirectory, keepSource));
            Assert.True(plan.IsSuccess);
            return (await relocation.ScheduleAsync(plan.Value)).IsSuccess;
        }

        public async Task<StartupOutcome> RunStartupAsync()
        {
            var configuration = AppConfiguration.Load(Paths);
            var hostSettings = new HostSettingSource(Paths, configuration);
            await StartupStorageRelocation.RunAsync(Paths, configuration, hostSettings);
            using var result = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(ConfigDirectory, StorageRelocationFiles.ResultFileName)));
            var root = result.RootElement;
            return new(root.GetProperty("success").GetBoolean(), root.GetProperty("message").GetString() ?? string.Empty);
        }

        public string? PersistedLocation()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Paths.ConfigPath));
            var root = document.RootElement;
            if (root.TryGetProperty("settings", out var settings)
                && settings.TryGetProperty("storage.location", out var overridden))
                return overridden.GetString();
            return root.TryGetProperty("storage", out var storage)
                && storage.TryGetProperty("location", out var location)
                ? location.GetString()
                : null;
        }

        public Task<string> ReadTargetAsync() => ReadAsync(TargetDatabase);

        public async Task<string> ReadAsync(string path)
        {
            await using var module = new SqliteModule(new(path), new Diagnostics());
            await module.ActivateAsync();
            try
            {
                var rows = await module.Session.QueryAsync(new("SELECT value FROM sample"));
                return Assert.Single(rows.Value).String("value");
            }
            finally { await module.StopAsync(); }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mosaic.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Mosaic.sln not found.");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
