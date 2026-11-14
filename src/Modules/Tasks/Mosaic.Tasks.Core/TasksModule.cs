using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Core.Internal;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core;

public sealed record TasksDependencies(
    ITaskRepository Repository,
    TimeProvider Clock,
    IIdGenerator Ids,
    IModuleExecutionGate Gate,
    IDiagnosticSink Diagnostics,
    Mosaic.Settings.Contracts.ISettingsApi? Settings = null);

public sealed class TasksModule : IModuleActivation
{
    private readonly IModuleExecutionGate _gate;
    private readonly TaskRules _rules = new();
    private readonly Mosaic.Settings.Contracts.ISettingsApi? _settings;
    public TasksModule(TasksDependencies dependencies)
    {
        _gate = dependencies.Gate;
        _settings = dependencies.Settings;
        Api = new GuardedTasksApi(new TaskService(dependencies.Repository, dependencies.Clock, dependencies.Ids, _rules), _gate, dependencies.Diagnostics);
    }
    public static ModuleManifest Definition { get; } = new(
        "tasks-core",
        [new("tasks.api", 1)],
        [new("tasks.repository", 1), new("settings.api", 1)],
        "Owns task rules and the guarded public task API.");
    public ModuleManifest Manifest => Definition;
    public ITasksApi Api { get; }
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settings is not null)
        {
            var values = await _settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
            if (!values.IsSuccess) throw new InvalidOperationException("Task settings could not be loaded.", new Exception(values.Error?.Message));
            _rules.Apply(values.Value);
        }
        _gate.Activate();
    }
    public Task StopAsync() => _gate.StopAsync();
    public async ValueTask DisposeAsync() => await _gate.StopAsync().ConfigureAwait(false);
}
