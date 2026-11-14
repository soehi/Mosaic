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
