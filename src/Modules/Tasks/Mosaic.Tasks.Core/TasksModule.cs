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
    Mosaic.Settings.Contracts.ISettingsCore? Settings = null,
    ITaskTransferRepository? TransferRepository = null);

public sealed class TasksModule : IModuleActivation
{
    private readonly IModuleExecutionGate _gate;
    private readonly TaskChangeSource _changes;
    private readonly TaskAutoArchive _archive;
    private readonly TaskRules _rules = new();
    private readonly Mosaic.Settings.Contracts.ISettingsCore? _settings;
    private IDisposable? _settingsRegistration;
    public TasksModule(TasksDependencies dependencies)
    {
        _gate = dependencies.Gate;
        _settings = dependencies.Settings;
        _changes=new(dependencies.Diagnostics);
        var api = new GuardedTasksApi(new TaskService(dependencies.Repository, dependencies.Clock, dependencies.Ids, _rules, _changes.Publish), _gate, dependencies.Diagnostics);
        _archive = new(dependencies.Repository, api, dependencies.Clock, dependencies.Diagnostics);
        Api = api;
        CreateOnceApi = api;
        Transfer = dependencies.TransferRepository is { } transfer
            ? new GuardedTaskTransferApi(new TaskTransferService(api, transfer, _rules, _changes.Publish), _gate, dependencies.Diagnostics)
            : null;
    }
    public static ModuleManifest Definition { get; } = new(
        "tasks-core",
        [new("tasks.api", 1), new("tasks.create-once", 1), new("tasks.changes",1), new("tasks.transfer", 1)],
        [new("tasks.repository", 1), new("settings.api", 1)],
        "Owns task rules and the guarded public task API.");
    public ModuleManifest Manifest => Definition;
    public ITasksApi Api { get; }
    public ITaskChangeSource Changes=>_changes;
    public ITaskCreateOnceApi CreateOnceApi { get; }
    public ITaskTransferApi? Transfer { get; }
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, object?>? initial = null;
        if (_settings is not null)
        {
            _settingsRegistration ??= _settings.Register(new TaskSettingsSource(_rules, _archive));
            var values = await _settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
            if (!values.IsSuccess) throw new InvalidOperationException("Task settings could not be loaded.", new Exception(values.Error?.Message));
            _rules.Apply(values.Value);
            initial = values.Value;
        }
        _gate.Activate();
        if (initial is not null) await _archive.ApplyAsync(initial).ConfigureAwait(false);
    }
    public async Task StopAsync()
    {
        if (_settings is not null && _settingsRegistration is not null)
        {
            var result = await _settings.PrepareSourceStopAsync("tasks-core").ConfigureAwait(false);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        }
        _settingsRegistration?.Dispose();
        _settingsRegistration = null;
        await _archive.SetDelayAsync(0).ConfigureAwait(false);
        await _gate.StopAsync().ConfigureAwait(false);
        _changes.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
