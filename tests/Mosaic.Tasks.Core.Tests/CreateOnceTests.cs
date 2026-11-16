using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

public sealed class CreateOnceTests
{
    private sealed class Gate : IModuleExecutionGate, IDisposable
    {
        private bool _active;
        public void Activate() => _active = true;
        public IDisposable? TryEnter() => _active ? this : null;
        public Task StopAsync() { _active = false; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Ids : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception exception) { } }

    private sealed class Settings : ISettingsCore
    {
        public Dictionary<string, object?> Values { get; } = [];
        public Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<SettingDefinition>>.Success([]));
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(Values));
        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
            IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(new Dictionary<string, OperationError>()));
        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> ResetAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public IDisposable Register(ISettingSource source) => new Registration();
        private sealed class Registration : IDisposable { public void Dispose() { } }
    }

    private static async Task<TasksModule> Start(MemoryTaskRepository repository, Settings? settings = null)
    {
        var module = new TasksModule(new(repository, TimeProvider.System, new Ids(), new Gate(), new Diagnostics(), settings));
        await module.ActivateAsync();
        return module;
    }

    [Fact]
    public async Task Explicit_initial_state_and_original_receipt_survive_limits_edits_and_deletion()
    {
        var repository = new MemoryTaskRepository();
        var settings = new Settings { Values = { ["tasks.defaultCompleted"] = true } };
        var request = new CreateTaskOnceRequest("source", "key", "  更新简要  ", "complete notes", false);
        Guid id;
        await using (var module = await Start(repository, settings))
        {
            var created = await module.CreateOnceApi.CreateOnceAsync(request);
            Assert.True(created.IsSuccess);
            Assert.Equal(TaskCreateOnceDisposition.Created, created.Value.Disposition);
            id = created.Value.TaskId;
            var task = (await module.Api.GetAsync(id)).Value;
            Assert.False(task.IsCompleted);
            Assert.Equal("更新简要", task.Title);
            await module.Api.UpdateAsync(new(id, "user edit", "user notes", 1));
            await module.Api.SetCompletedAsync(new(id, true, 2));
            var repeat = await module.CreateOnceApi.CreateOnceAsync(request with { Title = "更新简要" });
            Assert.Equal(id, repeat.Value.TaskId);
            Assert.Equal(TaskCreateOnceDisposition.AlreadyCreated, repeat.Value.Disposition);
            Assert.Equal("user edit", (await module.Api.GetAsync(id)).Value.Title);
            Assert.Equal(ErrorCodes.Conflict, (await module.CreateOnceApi.CreateOnceAsync(request with { IsCompleted = true })).Error?.Code);
            await module.Api.DeleteAsync(new(id, 3));
        }
        settings.Values["tasks.title.maxCharacters"] = 2;
        settings.Values["tasks.notes.maxCharacters"] = 1;
        await using var reopened = await Start(repository, settings);
        var historical = await reopened.CreateOnceApi.CreateOnceAsync(request);
        Assert.True(historical.IsSuccess);
        Assert.False(historical.Value.TaskExists);
        Assert.Equal(id, historical.Value.TaskId);
        Assert.Equal(ErrorCodes.Conflict, (await reopened.CreateOnceApi.CreateOnceAsync(request with { Notes = "changed" })).Error?.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await reopened.CreateOnceApi.CreateOnceAsync(request with { RequestKey = "new" })).Error?.Code);
        Assert.Single((await reopened.CreateOnceApi.ListReceiptsAsync("source")).Value);
        Assert.Empty((await reopened.Api.ListAsync(new())).Value);
    }

    [Fact]
    public async Task Invalid_unavailable_canceled_and_storage_errors_are_explicit_without_receipts()
    {
        var repository = new MemoryTaskRepository();
        await using var module = await Start(repository);
        var request = new CreateTaskOnceRequest("source", "key", "更新简要", "", false);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.CreateOnceApi.CreateOnceAsync(null!)).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.CreateOnceApi.CreateOnceAsync(request with { SourceId = " " })).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.CreateOnceApi.CreateOnceAsync(request with { RequestKey = "key\n" })).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.CreateOnceApi.ListReceiptsAsync(null!)).Error?.Code);
        Assert.Equal(ErrorCodes.Canceled, (await module.CreateOnceApi.CreateOnceAsync(request, new CancellationToken(true))).Error?.Code);
        repository.Failure = new(ErrorCodes.StorageUnavailable, "receipt read failed");
        Assert.Equal(ErrorCodes.StorageUnavailable, (await module.CreateOnceApi.CreateOnceAsync(request)).Error?.Code);
        Assert.Equal(ErrorCodes.StorageUnavailable, (await module.CreateOnceApi.ListReceiptsAsync("source")).Error?.Code);
        repository.Failure = null;
        Assert.Empty((await module.CreateOnceApi.ListReceiptsAsync("source")).Value);
        await module.StopAsync();
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.CreateOnceApi.CreateOnceAsync(request)).Error?.Code);
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.CreateOnceApi.ListReceiptsAsync("source")).Error?.Code);
    }
}
