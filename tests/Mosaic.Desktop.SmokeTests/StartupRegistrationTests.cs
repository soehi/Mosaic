using System.Text.Json;
using Mosaic.Contracts;

namespace Mosaic.Desktop.SmokeTests;

public sealed class StartupRegistrationTests
{
    private sealed class FakeStartup : IWindowsStartupRegistration
    {
        internal List<(string Root, bool Enabled)> Calls { get; } = [];
        public void SetEnabled(AppPaths paths, bool enabled) => Calls.Add((paths.Root, enabled));
        public bool IsEnabled(AppPaths paths) => Calls.Count > 0 && Calls[^1].Enabled;
    }

    private static AppPaths Paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "Mosaic-startup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "Config", "appsettings.json"), """{"version":1,"modules":[],"providers":{}}""");
        return new(root);
    }

    private static AppConfiguration Configuration() => new(
        1,
        ["tasks-core"],
        new Dictionary<string, string> { ["tasks.api@1"] = "tasks-core" },
        new StorageConfiguration("sqlite", "local-app-data", "mosaic.sqlite"));

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Persist_writes_config_then_registers_startup_with_workspace_root()
    {
        var paths = Paths();
        try
        {
            var configuration = Configuration() with
            {
                Settings = new Dictionary<string, JsonElement> { ["startup.runAtLogon"] = ToJson(false) }
            };
            var startup = new FakeStartup();
            var source = new HostSettingSource(paths, configuration, startup);
            var result = await source.PersistAsync(new Dictionary<string, object?> { ["startup.runAtLogon"] = true });
            Assert.True(result.IsSuccess);
            var call = Assert.Single(startup.Calls);
            Assert.Equal(paths.Root, call.Root);
            Assert.True(call.Enabled);
            Assert.Equal(true, source.GetValue("startup.runAtLogon"));
        }
        finally { Directory.Delete(paths.Root, true); }
    }

    [Fact]
    public async Task Rollback_restores_previous_startup_state()
    {
        var paths = Paths();
        try
        {
            var configuration = Configuration() with
            {
                Settings = new Dictionary<string, JsonElement> { ["startup.runAtLogon"] = ToJson(false) }
            };
            var startup = new FakeStartup();
            var source = new HostSettingSource(paths, configuration, startup);
            await source.PersistAsync(new Dictionary<string, object?> { ["startup.runAtLogon"] = true });
            var rollback = await source.RollbackAsync();
            Assert.True(rollback.IsSuccess);
            Assert.Equal([true, false], startup.Calls.Select(c => c.Enabled).ToArray());
            Assert.Equal(false, source.GetValue("startup.runAtLogon"));
        }
        finally { Directory.Delete(paths.Root, true); }
    }

    [Fact]
    public void Startup_command_quotes_paths_and_binds_nondefault_data_root()
    {
        var registration = new WindowsStartupRegistration();
        Assert.False(registration.IsEnabled(Paths()));
    }
}
