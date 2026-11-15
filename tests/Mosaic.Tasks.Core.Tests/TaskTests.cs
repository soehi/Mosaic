using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Core.Tests;

public sealed class TaskTests
{
    private sealed class Gate : IModuleExecutionGate, IDisposable
    {
        private bool _active;
        public void Activate() => _active = true;
        public IDisposable? TryEnter() => _active ? this : null;
        public Task StopAsync() { _active = false; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Ids : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string correlationId, Exception exception) { } }
    private sealed class Settings : Mosaic.Settings.Contracts.ISettingsCore
    {
        public Dictionary<string, object?> Values { get; } = [];
        public List<string> RegisteredModules { get; } = [];
        public Task<OperationResult<IReadOnlyList<Mosaic.Settings.Contracts.SettingDefinition>>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<Mosaic.Settings.Contracts.SettingDefinition>>.Success([]));
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> GetValuesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values, StringComparer.Ordinal)));
        public Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
            IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, OperationError>>.Success(new Dictionary<string, OperationError>()));
        public Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public IDisposable Register(Mosaic.Settings.Contracts.ISettingSource source)
        {
            RegisteredModules.Add(source.ModuleId);
            return new Registration(() => RegisteredModules.Remove(source.ModuleId));
        }
        private sealed class Registration(Action onDispose) : IDisposable
        {
            private int _disposed;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(); }
        }
    }
    private static async Task<TasksModule> Start(MemoryTaskRepository repository, Gate? gate = null)
    {
        var module = new TasksModule(new(repository, new Clock(), new Ids(), gate ?? new Gate(), new Diagnostics()));
        await module.ActivateAsync(); return module;
    }
    [Fact]
    public async Task Crud_preserves_identity_creation_time_and_returns_snapshots()
    {
        await using var module = await Start(new());
        var api = module.Api;
        var created = (await api.CreateAsync(new("  第一项  ", "备注"))).Value;
        Assert.Equal("第一项", created.Title);
        Assert.Equal(1, created.Revision); Assert.False(created.IsCompleted);
        var updated = (await api.UpdateAsync(new(created.Id, "更新", "新备注", 1))).Value;
        Assert.Equal(created.Id, updated.Id); Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc); Assert.Equal(2, updated.Revision);
        var done = (await api.SetCompletedAsync(new(created.Id, true, 2))).Value;
        Assert.True(done.IsCompleted); Assert.Equal(3, done.Revision);
        Assert.Empty((await api.ListAsync(new(TaskFilter.Open))).Value);
        Assert.Single((await api.ListAsync(new(TaskFilter.Completed))).Value);
        Assert.True((await api.SetCompletedAsync(new(done.Id, false, 3))).IsSuccess);
        Assert.True((await api.DeleteAsync(new(done.Id, 4))).IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, (await api.GetAsync(done.Id)).Error?.Code);
        Assert.Equal("第一项", created.Title);
    }
    [Fact]
    public async Task Unicode_limits_count_code_points()
    {
        await using var module = await Start(new());
        Assert.True((await module.Api.CreateAsync(new(string.Concat(Enumerable.Repeat("😀", 200)), ""))).IsSuccess);
        var title = await module.Api.CreateAsync(new(new string('中', 201), ""));
        Assert.Equal(ErrorCodes.ValidationFailed, title.Error?.Code); Assert.Equal("Title", title.Error?.Field);
        Assert.Equal("Title", (await module.Api.CreateAsync(new(" \t", ""))).Error?.Field);
        Assert.Equal("Notes", (await module.Api.CreateAsync(new("有效", new string('a', 20_001)))).Error?.Field);
    }
    [Fact]
    public async Task Invalid_requests_cancellation_and_unavailable_gate_are_explicit()
    {
        await using var module = await Start(new());
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.Api.CreateAsync(null!)).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.Api.CreateAsync(new(null!, ""))).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.Api.ListAsync(new((TaskFilter)999))).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.Api.GetAsync(Guid.Empty)).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await module.Api.DeleteAsync(new(Guid.NewGuid(), 0))).Error?.Code);
        Assert.Equal(ErrorCodes.Canceled, (await module.Api.CreateAsync(new("a", ""), new CancellationToken(true))).Error?.Code);
        await module.StopAsync();
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.Api.ListAsync(new())).Error?.Code);
    }
    [Fact]
    public async Task Module_controls_its_gate_from_activation_through_stop()
    {
        await using var module = new TasksModule(new(new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics()));
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.Api.ListAsync(new())).Error?.Code);
        await module.ActivateAsync();
        Assert.True((await module.Api.CreateAsync(new("a", ""))).IsSuccess);
        await module.StopAsync();
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.Api.ListAsync(new())).Error?.Code);
    }

    [Fact]
    public async Task Activation_registers_its_source_before_reading_settings_and_revokes_on_stop()
    {
        var settings = new Settings
        {
            Values =
            {
                ["tasks.title.maxCharacters"] = 50,
                ["tasks.notes.maxCharacters"] = 5000,
                ["tasks.title.required"] = false,
                ["tasks.defaultCompleted"] = true
            }
        };
        await using var module = new TasksModule(new(new MemoryTaskRepository(), new Clock(), new Ids(), new Gate(), new Diagnostics(), settings));
        await module.ActivateAsync();
        Assert.Contains("tasks-core", settings.RegisteredModules);
        var constraints = (await module.Api.GetConstraintsAsync()).Value!;
        Assert.Equal(50, constraints.TitleMaxCharacters);
        Assert.Equal(5000, constraints.NotesMaxCharacters);
        Assert.False(constraints.TitleRequired);
        Assert.True(constraints.DefaultCompleted);
        await module.StopAsync();
        Assert.DoesNotContain("tasks-core", settings.RegisteredModules);
    }
    [Fact]
    public async Task Stale_revision_and_deleted_targets_are_conflicts()
    {
        await using var module = await Start(new());
        var task = (await module.Api.CreateAsync(new("a", ""))).Value;
        Assert.True((await module.Api.UpdateAsync(new(task.Id, "b", "", 1))).IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, (await module.Api.UpdateAsync(new(task.Id, "c", "", 1))).Error?.Code);
        Assert.True((await module.Api.DeleteAsync(new(task.Id, 2))).IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, (await module.Api.UpdateAsync(new(task.Id, "d", "", 2))).Error?.Code);
        Assert.Equal(ErrorCodes.Conflict, (await module.Api.DeleteAsync(new(task.Id, 2))).Error?.Code);
    }
    [Fact]
    public async Task Storage_failure_is_never_a_success()
    {
        var repo = new MemoryTaskRepository { Failure = new(ErrorCodes.StorageUnavailable, "Failed") };
        await using var module = await Start(repo);
        Assert.Equal(ErrorCodes.StorageUnavailable, (await module.Api.CreateAsync(new("a", ""))).Error?.Code);
        Assert.Equal(ErrorCodes.StorageUnavailable, (await module.Api.ListAsync(new())).Error?.Code);
        Assert.Equal(ErrorCodes.StorageUnavailable, (await module.Api.GetAsync(Guid.NewGuid())).Error?.Code);
    }
    [Fact]
    public async Task Repository_order_is_passed_through_unchanged()
    {
        var repository = new MemoryTaskRepository
        {
            Ordering = tasks => tasks.OrderBy(task => task.CreatedAtUtc)
        };
        var older = new TaskItemDto(Guid.NewGuid(), "older", "", false, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 1);
        var newer = older with { Id = Guid.NewGuid(), Title = "newer", CreatedAtUtc = DateTimeOffset.UtcNow };
        await repository.InsertAsync(older);
        await repository.InsertAsync(newer);
        await using var module = await Start(repository);
        Assert.Equal(new[] { older.Id, newer.Id }, (await module.Api.ListAsync(new())).Value.Select(task => task.Id));
    }
    [Fact]
    public async Task Inactive_module_rejects_retained_api()
    {
        await using var module = await Start(new());
        await module.StopAsync();
        Assert.Equal(ErrorCodes.CapabilityUnavailable, (await module.Api.CreateAsync(new("a", ""))).Error?.Code);
    }
}
