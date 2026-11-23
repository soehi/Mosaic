using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Settings.Sqlite;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Settings.Sqlite.Tests;

public sealed class SettingsRepositoryTests
{
    private sealed class Diagnostics : IDiagnosticSink
    {
        public void Report(string correlationId, Exception exception) { }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Startup_migration_classifies_and_preserves_choice_on_reopen(bool existingConfiguration, bool expectedChoice)
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using (var storage = new SqliteModule(new(path, ConfigurationExisted: existingConfiguration), new Diagnostics()))
            {
                await storage.ActivateAsync();
                await using var settings = new SettingsSqliteModule(new(storage.Session));
                await settings.ActivateAsync();
                var state = await ((Mosaic.Storage.Contracts.IInstallationState)storage.Session).GetInstallationKindAsync();
                Assert.True(state.IsSuccess);
                Assert.True((await settings.Repository.InitializeAsync(new("test-choice-v1", "test.choice", state.Value == Mosaic.Storage.Contracts.InstallationKind.Legacy))).IsSuccess);
                var loaded = await settings.Repository.LoadAsync();
                Assert.Equal(expectedChoice, loaded.Value["test.choice"]);
                Assert.True((await settings.Repository.SaveAsync(new Dictionary<string, object?> { ["test.choice"] = false })).IsSuccess);
            }
            await using var reopened = new SqliteModule(new(path), new Diagnostics());
            await reopened.ActivateAsync();
            await using var reopenedSettings = new SettingsSqliteModule(new(reopened.Session));
            await reopenedSettings.ActivateAsync();
            Assert.True((await reopenedSettings.Repository.InitializeAsync(new("test-choice-v1", "test.choice", true))).IsSuccess);
            var again = await reopenedSettings.Repository.LoadAsync();
            Assert.Equal(false, again.Value["test.choice"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Removed_account_choice_setting_is_deleted_and_other_settings_survive()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var storage = new SqliteModule(new(path), new Diagnostics());
            await storage.ActivateAsync();
            Assert.True((await storage.Session.ExecuteAsync(new(
                "CREATE TABLE settings (setting_id TEXT PRIMARY KEY, value_json TEXT NOT NULL)"))).IsSuccess);
            Assert.True((await storage.Session.ExecuteAsync(new(
                "INSERT INTO settings(setting_id,value_json) VALUES ('auth.accountChoiceMade','true'),('auth.serverUrl','\"https://example.invalid\"')"))).IsSuccess);

            await using var settings = new SettingsSqliteModule(new(storage.Session));
            await settings.ActivateAsync();

            var loaded = await settings.Repository.LoadAsync();
            Assert.True(loaded.IsSuccess, loaded.Error?.Message);
            Assert.False(loaded.Value.ContainsKey("auth.accountChoiceMade"));
            Assert.Equal("https://example.invalid", loaded.Value["auth.serverUrl"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Existing_database_with_tasks_and_no_settings_is_legacy()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using (var storage = new SqliteModule(new(path, ConfigurationExisted: false), new Diagnostics()))
            {
                await storage.ActivateAsync();
                Assert.True((await storage.Session.ExecuteAsync(new("CREATE TABLE legacy_tasks (title TEXT NOT NULL)"))).IsSuccess);
                Assert.True((await storage.Session.ExecuteAsync(new("INSERT INTO legacy_tasks(title) VALUES ('existing task')"))).IsSuccess);
                Assert.True((await storage.Session.ExecuteAsync(new("DELETE FROM mosaic_startup_metadata"))).IsSuccess);
            }
            await using var reopened = new SqliteModule(new(path, ConfigurationExisted: false), new Diagnostics());
            await reopened.ActivateAsync();
            var state = await ((Mosaic.Storage.Contracts.IInstallationState)reopened.Session).GetInstallationKindAsync();
            Assert.Equal(Mosaic.Storage.Contracts.InstallationKind.Legacy, state.Value);
            await using var settings = new SettingsSqliteModule(new(reopened.Session));
            await settings.ActivateAsync();
            Assert.True((await settings.Repository.InitializeAsync(new("test-choice-v1", "test.choice", true))).IsSuccess);
            Assert.Equal(true, (await settings.Repository.LoadAsync()).Value["test.choice"]);
            Assert.Single((await reopened.Session.QueryAsync(new("SELECT title FROM legacy_tasks"))).Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Failed_startup_migration_rolls_back_and_retries()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var storage = new SqliteModule(new(path, ConfigurationExisted: false), new Diagnostics());
            await storage.ActivateAsync();
            await using var settings = new SettingsSqliteModule(new(storage.Session));
            await settings.ActivateAsync();
            Assert.True((await storage.Session.ExecuteAsync(new("CREATE TABLE settings_initializations (migration_id TEXT PRIMARY KEY)"))).IsSuccess);
            Assert.True((await storage.Session.ExecuteAsync(new("CREATE TRIGGER reject_completion BEFORE INSERT ON settings_initializations BEGIN SELECT RAISE(ABORT, 'test failure'); END"))).IsSuccess);
            var failed = await settings.Repository.InitializeAsync(new("test-choice-v1", "test.choice", false));
            Assert.False(failed.IsSuccess);
            var choice = await storage.Session.QueryAsync(new("SELECT value_json FROM settings WHERE setting_id = 'auth.accountChoiceMade'"));
            Assert.True(choice.IsSuccess);
            Assert.Empty(choice.Value);
            Assert.True((await storage.Session.ExecuteAsync(new("DROP TRIGGER reject_completion"))).IsSuccess);
            Assert.True((await settings.Repository.InitializeAsync(new("test-choice-v1", "test.choice", false))).IsSuccess);
            var loaded = await settings.Repository.LoadAsync();
            Assert.Equal(false, loaded.Value["test.choice"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Settings_persist_through_migration_and_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using (var storage = new SqliteModule(new(path), new Diagnostics()))
            {
                await storage.ActivateAsync();
                await using var settings = new SettingsSqliteModule(new(storage.Session));
                await settings.ActivateAsync();
                Assert.True((await settings.Repository.SaveAsync(new Dictionary<string, object?>
                {
                    ["tasks.confirmDelete"] = false,
                    ["tasks.title.maxCharacters"] = 150,
                    ["appearance.theme"] = "Dark"
                })).IsSuccess);
            }

            await using (var storage = new SqliteModule(new(path), new Diagnostics()))
            {
                await storage.ActivateAsync();
                await using var settings = new SettingsSqliteModule(new(storage.Session));
                await settings.ActivateAsync();
                var loaded = await settings.Repository.LoadAsync();
                Assert.True(loaded.IsSuccess);
                Assert.Equal(false, loaded.Value!["tasks.confirmDelete"]);
                Assert.Equal(150L, loaded.Value!["tasks.title.maxCharacters"]);
                Assert.Equal("Dark", loaded.Value!["appearance.theme"]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
