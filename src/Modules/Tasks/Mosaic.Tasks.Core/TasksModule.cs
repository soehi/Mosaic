using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core.Internal;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core;

public sealed record TasksDependencies(ITaskRepository Repository, TimeProvider Clock, IIdGenerator Ids, IModuleExecutionGate Gate, IDiagnosticSink Diagnostics);

public sealed class TasksModule : IModuleActivation
{
    private readonly IModuleExecutionGate _gate;
    public TasksModule(TasksDependencies dependencies)
    {
        _gate = dependencies.Gate;
        Api = new GuardedTasksApi(new TaskService(dependencies.Repository, dependencies.Clock, dependencies.Ids), _gate, dependencies.Diagnostics);
    }
    public static ModuleManifest Definition { get; } = new(
        "tasks-core",
        [new("tasks.api", 1)],
        [new("tasks.repository", 1)],
        "Owns task rules and the guarded public task API.");
    public ModuleManifest Manifest => Definition;
    public ITasksApi Api { get; }
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _gate.Activate();
        return Task.CompletedTask;
    }
    public Task StopAsync() => _gate.StopAsync();
    public async ValueTask DisposeAsync() => await _gate.StopAsync().ConfigureAwait(false);
}
