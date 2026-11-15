using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core.Internal;

namespace Mosaic.Settings.Core;

public sealed record SettingsDependencies(
    ISettingRepository Repository,
    IHostSettingSource? HostSource = null,
    IModuleExecutionGate? Gate = null,
    IDiagnosticSink? Diagnostics = null,
    IReadOnlyList<ISettingSource>? DeferredSources = null);

public sealed class SettingsModule : IModuleActivation
{
    private readonly IModuleExecutionGate? _gate;
    private readonly SettingsService _service;
    private readonly IReadOnlyList<ISettingSource>? _deferredSources;

    public SettingsModule(SettingsDependencies dependencies)
        : this(dependencies, new(dependencies.Repository, dependencies.HostSource)) { }

    internal SettingsModule(SettingsDependencies dependencies, SettingsService service)
    {
        _gate = dependencies.Gate;
        _service = service;
        _deferredSources = dependencies.DeferredSources;
        Api = new GuardedSettingsApi(service, dependencies.Gate, dependencies.Diagnostics);
    }

    public static ModuleManifest Definition { get; } = new(
        "settings-core",
        [new("settings.api", 1)],
        [new("settings.repository", 1), new("sqlite.session", 1)],
        "Owns setting definitions, validation and persistence routing.");
    public ModuleManifest Manifest => Definition;
    public ISettingsCore Api { get; }
    internal SettingsService Service => _service;
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _gate?.Activate();
        if (_deferredSources is not null)
            foreach (var source in _deferredSources) _service.Register(source);
        return Task.CompletedTask;
    }
    public Task StopAsync() => _gate?.StopAsync() ?? Task.CompletedTask;
    public async ValueTask DisposeAsync() { if (_gate is not null) await _gate.StopAsync().ConfigureAwait(false); }
}
