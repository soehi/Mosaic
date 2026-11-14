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
        public IReadOnlyList<SettingDefinition> Definitions => SettingCatalog.ForModule("host");
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
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Applied { get; } = [];
        public Source(string moduleId, IReadOnlyList<SettingDefinition> definitions) { ModuleId = moduleId; Definitions = definitions; }
        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Catalog_has_planned_definitions_and_unique_ids()
    {
        Assert.Equal(54, SettingCatalog.Definitions.Count);
        Assert.Equal(54, SettingCatalog.Definitions.Select(item => item.Id).Distinct().Count());
        Assert.Contains(SettingCatalog.Definitions, item => item.Id == "appearance.theme");
        Assert.Contains(SettingCatalog.Definitions, item => item.Id == "diagnostics.singleInstanceRetryMilliseconds");
        Assert.Contains(SettingCatalog.Definitions, item => item.Id == "ui.text.restoredSuccess");
    }

    [Fact]
    public async Task Rejects_unknown_and_invalid_values()
    {
        var repository = new MemoryRepository();
        await using var module = new SettingsModule(new(repository));
        await module.ActivateAsync();
        var unknown = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["missing"] = true });
        Assert.Equal(ErrorCodes.InvalidRequest, unknown.Value!["missing"].Code);
        Assert.Empty(repository.Values);

        var outOfRange = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["tasks.title.maxCharacters"] = 1001 });
        Assert.Equal(ErrorCodes.ValidationFailed, outOfRange.Value!["tasks.title.maxCharacters"].Code);

        var invalidEnum = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["appearance.theme"] = "Neon" });
        Assert.Equal(ErrorCodes.ValidationFailed, invalidEnum.Value!["appearance.theme"].Code);
    }

    [Fact]
    public async Task Routes_user_settings_and_host_settings_separately()
    {
        var repository = new MemoryRepository();
        var host = new HostSource();
        await using var module = new SettingsModule(new(repository, host));
        await module.ActivateAsync();
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
        await module.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["tasks.confirmDelete"] = false,
            ["tasks.sortOrder"] = "TitleAsc",
            ["logging.level"] = "Warning"
        });
        Assert.True((await module.Api.ResetAsync("tasks-ui")).IsSuccess);
        Assert.DoesNotContain("tasks.confirmDelete", repository.Values);
        Assert.Equal("TitleAsc", repository.Values["tasks.sortOrder"]);
        Assert.Equal("Warning", host.Values["logging.level"]);
        Assert.True((await module.Api.ResetAsync()).IsSuccess);
        Assert.DoesNotContain("tasks.sortOrder", repository.Values);
        Assert.Equal("Information", host.Values["logging.level"]);
    }

    [Fact]
    public async Task Storage_failure_is_not_applied_as_success()
    {
        var repository = new MemoryRepository { Failure = new(ErrorCodes.StorageUnavailable, "failed") };
        await using var module = new SettingsModule(new(repository));
        await module.ActivateAsync();
        var result = await module.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["tasks.confirmDelete"] = false });
        Assert.Equal(ErrorCodes.StorageUnavailable, result.Error?.Code);
    }
}
