using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Storage.Contracts;
using Mosaic.Settings.Sqlite.Internal;

namespace Mosaic.Settings.Sqlite;

public sealed record SettingsSqliteDependencies(IDatabaseSession Session);

public sealed class SettingsSqliteModule : IModuleActivation
{
    private readonly IDatabaseSession _session;
    private SettingsRepository? _repository;

    public SettingsSqliteModule(SettingsSqliteDependencies dependencies) => _session = dependencies.Session;

    public static ModuleManifest Definition { get; } = new(
        "settings-sqlite",
        [new("settings.repository", 1)],
        [new("sqlite.session", 1)],
        "Owns the settings table migration and parameterized setting persistence.");
    public ModuleManifest Manifest => Definition;
    public ISettingRepository Repository => _repository ?? throw new InvalidOperationException("Settings storage is not active.");

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is not null) throw new InvalidOperationException("Settings storage already started.");
        _repository = new(_session);
        var migration = await _session.MigrateAsync(Definition.Id,
            [new(1, Embedded.Get("Mosaic.Settings.Sqlite.Migrations.001_CreateSettings.sql"))], cancellationToken).ConfigureAwait(false);
        if (!migration.IsSuccess) throw new InvalidOperationException("Settings migration failed.", new Exception(migration.Error?.Message));
    }

    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
