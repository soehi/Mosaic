using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;

namespace Mosaic.Settings.Core.Tests;

public sealed class SettingsTests
{
    private sealed class MemoryRepository : ISettingRepository
    {
        public Dictionary<string, object?> Values { get; } = [];
        public OperationError? Failure { get; set; }
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Failure is null
                ? OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values, StringComparer.Ordinal))
                : OperationResult<IReadOnlyDictionary<string, object?>>.Failure(Failure));
        public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromResult(OperationResult.Failure(Failure));
            foreach (var (id, value) in values) Values[id] = value;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromResult(OperationResult.Failure(Failure));
            foreach (var id in ids) Values.Remove(id);
            return Task.FromResult(OperationResult.Success());
        }
    }

    private sealed class HostSource : IHostSettingSource
    {
        public Dictionary<string, object?> Values { get; } = [];
        public OperationError? Failure { get; set; }
        public string ModuleId => "host";
        public IReadOnlyList<SettingDefinition> Definitions => SettingCatalog.Definitions;
        public object? GetValue(string id) => Values.GetValueOrDefault(id);
        public Task<OperationResult> PersistAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromResult(OperationResult.Failure(Failure));
            foreach (var (id, value) in values) Values[id] = value;
            return Task.FromResult(OperationResult.Success());
        }
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Source : ISettingSource
    {
        public string ModuleId { get; }
        public IReadOnlyList<SettingDefinition> Definitions { get; }
        public Source(string moduleId, IReadOnlyList<SettingDefinition> definitions) { ModuleId = moduleId; Definitions = definitions; }
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly SettingDefinition[] TaskCoreDefinitions =
    [
        new("tasks.sortOrder", "tasks-core", "Sort order", "Default task sorting", SettingValueKind.Enum, "CreatedDesc", null, null, ["CreatedDesc", "TitleAsc"],
            SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings),
        new("tasks.title.maxCharacters", "tasks-core", "Title limit", "Maximum title characters", SettingValueKind.Integer, 200, 1, 1000, null,
            SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings)
    ];

    private static readonly SettingDefinition[] TaskUiDefinitions =
    [
        new("tasks.confirmDelete", "tasks-ui", "Confirm delete", "Confirm before delete", SettingValueKind.Boolean, true, null, null, null,
            SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings)
    ];

    private sealed class TaskCoreSource : ISettingSource
    {
        public string ModuleId => "tasks-core";
        public IReadOnlyList<SettingDefinition> Definitions => TaskCoreDefinitions;
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TaskUiSource : ISettingSource
    {
        public string ModuleId => "tasks-ui";
        public IReadOnlyList<SettingDefinition> Definitions => TaskUiDefinitions;
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Catalog_has_planned_definitions_and_unique_ids()
    {
        Assert.Equal(11, SettingCatalog.Definitions.Count);
        Assert.Equal(11, SettingCatalog.Definitions.Select(item => item.Id).Distinct().Count());
        Assert.Contains(SettingCatalog.Definitions, item => item.Id == "startup.width");
        Assert.Contains(SettingCatalog.Definitions, item => item.Id == "diagnostics.singleInstanceRetryMilliseconds");
    }

    [Fact]
    public async Task Host_modules_default_matches_the_canonical_boot_configuration()
    {
        var modules = (string[]?)SettingCatalog.Definitions.Single(item => item.Id == "host.modules").DefaultValue;
        Assert.NotNull(modules);
        Assert.Equal(7, modules.Length);
        Assert.Contains("settings-sqlite", modules);
        Assert.Contains("settings-core", modules);
        Assert.Contains("settings-ui", modules);
        Assert.Equal("storage-sqlite", modules[0]);
    }

    [Fact]
    public async Task Values_are_normalized_to_definition_kinds_on_read_and_write()
    {
        var repository = new MemoryRepository();
        repository.Values["tasks.title.maxCharacters"] = 250L;
        var host = new HostSource();
        host.Values["host.modules"] = new object?[] { "storage-sqlite", "tasks-core" };
        host.Values["host.providers"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tasks.api@1"] = "tasks-core" };
        host.Values["logging.retentionDays"] = 7L;
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskCoreSource());

        var values = (await module.Api.GetValuesAsync()).Value!;
        Assert.Equal(250, values["tasks.title.maxCharacters"]);
        Assert.Equal(new[] { "storage-sqlite", "tasks-core" }, Assert.IsType<string[]>(values["host.modules"]));
        var providers = Assert.IsType<Dictionary<string, string>>(values["host.providers"]);
        Assert.Equal("tasks-core", providers["tasks.api@1"]);
        Assert.Equal(7, values["logging.retentionDays"]);

        var proposed = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["tasks.title.maxCharacters"] = 300L });
        Assert.True(proposed.IsSuccess);
        Assert.Equal(300, repository.Values["tasks.title.maxCharacters"]);
    }

    [Fact]
    public async Task Rejects_duplicate_and_cross_module_definitions()
    {
        var repository = new MemoryRepository();
        await using var module = new SettingsModule(new(repository));
        await module.ActivateAsync();
        var api = (ISettingsCore)module.Api;
        api.Register(new TaskCoreSource());

        Assert.Throws<InvalidOperationException>(() => api.Register(new TaskCoreSource()));
        Assert.Throws<InvalidOperationException>(() => api.Register(new Source("tasks-ui", TaskCoreDefinitions)));
        Assert.Throws<InvalidOperationException>(() => api.Register(new Source("tasks-core",
        [
            TaskCoreDefinitions[0],
            TaskCoreDefinitions[0] with { DisplayName = "Duplicate id" }
        ])));
    }

    [Fact]
    public async Task Rejects_unknown_and_invalid_values()
    {
        var repository = new MemoryRepository();
        await using var module = new SettingsModule(new(repository));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskCoreSource());
        var unknown = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["missing"] = true });
        Assert.Equal(ErrorCodes.InvalidRequest, unknown.Value!["missing"].Code);
        Assert.Empty(repository.Values);

        await using var tasks = new SettingsModule(new(repository));
        await tasks.ActivateAsync();
        ((ISettingsCore)tasks.Api).Register(new TaskCoreSource());

        var outOfRange = await tasks.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["tasks.title.maxCharacters"] = 1001 });
        Assert.Equal(ErrorCodes.ValidationFailed, outOfRange.Value!["tasks.title.maxCharacters"].Code);
        Assert.Empty(repository.Values);
    }

    [Fact]
    public async Task Routes_user_settings_and_host_settings_separately()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskUiSource());
        var proposed = await module.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["tasks.confirmDelete"] = false,
            ["logging.level"] = "Warning"
        });
        Assert.True(proposed.IsSuccess);
        Assert.Equal(false, repository.Values["tasks.confirmDelete"]);
        Assert.Equal("Warning", host.Values["logging.level"]);
        Assert.Equal("Warning", (await module.Api.GetValuesAsync()).Value!["logging.level"]);
    }

    [Fact]
    public async Task Reset_clears_only_target_module_values()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskCoreSource());
        ((ISettingsCore)module.Api).Register(new TaskUiSource());
        await module.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["tasks.confirmDelete"] = false,
            ["tasks.sortOrder"] = "TitleAsc",
            ["logging.level"] = "Warning"
        });
        Assert.True((await module.Api.ResetAsync("tasks-core")).IsSuccess);
        Assert.DoesNotContain("tasks.sortOrder", repository.Values);
        Assert.Equal(false, repository.Values["tasks.confirmDelete"]);
        Assert.Equal("Warning", host.Values["logging.level"]);
        Assert.True((await module.Api.ResetAsync()).IsSuccess);
        Assert.DoesNotContain("tasks.confirmDelete", repository.Values);
        Assert.DoesNotContain("tasks.sortOrder", repository.Values);
        Assert.Equal("Information", host.Values["logging.level"]);
    }

    [Fact]
    public async Task Storage_failure_is_not_applied_as_success()
    {
        var repository = new MemoryRepository { Failure = new(ErrorCodes.StorageUnavailable, "failed") };
        await using var module = new SettingsModule(new(repository));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskUiSource());
        var result = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["tasks.confirmDelete"] = false });
        Assert.Equal(ErrorCodes.StorageUnavailable, result.Error?.Code);
    }

    [Fact]
    public async Task Host_persist_failure_rolls_back_user_settings()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskUiSource());
        Assert.True((await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["tasks.confirmDelete"] = false })).IsSuccess);

        host.Failure = new(ErrorCodes.StorageUnavailable, "disk full");
        var result = await module.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["tasks.confirmDelete"] = true,
            ["logging.level"] = "Warning"
        });
        Assert.Equal(ErrorCodes.StorageUnavailable, result.Error?.Code);
        Assert.Equal(false, repository.Values["tasks.confirmDelete"]);
        Assert.Empty(host.Values);
    }

    [Fact]
    public async Task Reset_rolls_back_user_values_when_host_persist_fails()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
        ((ISettingsCore)module.Api).Register(new TaskUiSource());
        await module.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["tasks.confirmDelete"] = false,
            ["logging.level"] = "Warning"
        });

        host.Failure = new(ErrorCodes.StorageUnavailable, "disk full");
        var result = await module.Api.ResetAsync();
        Assert.Equal(ErrorCodes.StorageUnavailable, result.Error?.Code);
        Assert.Equal(false, repository.Values["tasks.confirmDelete"]);
        Assert.Equal("Warning", host.Values["logging.level"]);
    }

    [Fact]
    public async Task Rejects_malformed_module_list_provider_map_and_database_filename()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
        var api = module.Api;

        var modules = await api.ProposeChangesAsync(new Dictionary<string, object?> { ["host.modules"] = new[] { "Bad Module" } });
        Assert.Equal(ErrorCodes.ValidationFailed, modules.Value!["host.modules"].Code);
        var duplicate = await api.ProposeChangesAsync(new Dictionary<string, object?> { ["host.modules"] = new[] { "tasks-core", "tasks-core" } });
        Assert.Equal(ErrorCodes.ValidationFailed, duplicate.Value!["host.modules"].Code);

        var providers = await api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["host.providers"] = new Dictionary<string, string> { ["sqlite.session"] = "storage-sqlite" }
        });
        Assert.Equal(ErrorCodes.ValidationFailed, providers.Value!["host.providers"].Code);
        var providerModule = await api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["host.providers"] = new Dictionary<string, string> { ["sqlite.session@1"] = "Storage Sqlite" }
        });
        Assert.Equal(ErrorCodes.ValidationFailed, providerModule.Value!["host.providers"].Code);

        var filename = await api.ProposeChangesAsync(new Dictionary<string, object?> { ["storage.filename"] = "../escape.sqlite" });
        Assert.Equal(ErrorCodes.ValidationFailed, filename.Value!["storage.filename"].Code);
        var valid = await api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["host.modules"] = new[] { "storage-sqlite", "tasks-core" },
            ["host.providers"] = new Dictionary<string, string> { ["sqlite.session@1"] = "storage-sqlite" },
            ["storage.filename"] = "mosaic.sqlite"
        });
        Assert.True(valid.IsSuccess);
    }

    [Fact]
    public async Task Deferred_sources_register_at_activation_and_their_values_are_validated_and_persisted()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        var storage = new Source("storage-sqlite",
        [
            new("storage.journalMode", "storage-sqlite", "Journal", "Journal mode", SettingValueKind.Enum, "Delete", null, null, ["Delete", "Wal"],
                SettingCategory.Advanced, SettingApplyMode.Restart, SettingPersistence.HostConfig)
        ]);
        await using var module = new SettingsModule(new(repository, host, DeferredSources: [storage]));

        var unknown = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["storage.journalMode"] = "Bogus" });
        Assert.Equal(ErrorCodes.InvalidRequest, unknown.Value!["storage.journalMode"].Code);

        await module.ActivateAsync();
        var definitions = await module.Api.ListDefinitionsAsync();
        Assert.Contains(definitions.Value!, definition => definition.Id == "storage.journalMode");
        Assert.Equal("storage-sqlite", definitions.Value!.Single(definition => definition.Id == "storage.journalMode").ModuleId);

        var invalid = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["storage.journalMode"] = "Bogus" });
        Assert.Equal(ErrorCodes.ValidationFailed, invalid.Value!["storage.journalMode"].Code);

        var proposed = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["storage.journalMode"] = "Wal" });
        Assert.True(proposed.IsSuccess);
        Assert.Equal("Wal", host.Values["storage.journalMode"]);
    }
}
