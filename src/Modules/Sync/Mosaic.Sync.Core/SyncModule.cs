using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Sync.Core.Internal;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Sync.Core;

public sealed record SyncDependencies(
    IAuthApi Auth,
    ISyncTransport Transport,
    ITaskTransferApi Tasks,
    ISettingsCore Settings,
    IModuleExecutionGate Gate,
    IDiagnosticSink Diagnostics);

public sealed class SyncModule : IModuleActivation
{
    private readonly SyncService _service;
    private readonly ISyncApi _api;
    private readonly IAuthApi _auth;
    private readonly ISettingsCore _settings;
    private readonly IModuleExecutionGate _gate;
    private readonly SyncOptions _options = new();
    private IDisposable? _settingsRegistration;
    private EventHandler? _authChanged;

    public SyncModule(SyncDependencies dependencies)
    {
        _auth = dependencies.Auth;
        _settings = dependencies.Settings;
        _gate = dependencies.Gate;
        _service = new(dependencies.Auth, dependencies.Transport, dependencies.Tasks, dependencies.Settings, _options);
        _api = new GuardedSyncApi(_service, dependencies.Gate, dependencies.Diagnostics);
    }

    public static ModuleManifest Definition { get; } = new(
        "sync-core",
        [new("sync.api", 1)],
        [new("auth.api", 1), new("sync.transport", 1), new("tasks.transfer", 1), new("settings.api", 1)],
        "Uploads and restores task and portable setting backups through the account server.");

    public ModuleManifest Manifest => Definition;

    public ISyncApi Api => _api;

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _settingsRegistration ??= _settings.Register(new SyncSettingsSource(_options));
        _authChanged ??= (_, _) => _service.RaiseForAuthChange();
        _auth.StateChanged += _authChanged;
        _gate.Activate();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_authChanged is not null) _auth.StateChanged -= _authChanged;
        var result = await _settings.PrepareSourceStopAsync("sync-core").ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        _settingsRegistration?.Dispose();
        _settingsRegistration = null;
        await _gate.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
