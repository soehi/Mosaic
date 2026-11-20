using Mosaic.Auth.Contracts;
using Mosaic.Auth.Core.Internal;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Auth.Core;

public sealed record AuthDependencies(
    IAuthTransport Transport,
    IAuthSecretStore Secrets,
    ISettingsCore Settings,
    TimeProvider Clock,
    IModuleExecutionGate Gate,
    IDiagnosticSink Diagnostics);

// Account support is optional for the app: the module never blocks startup on the network and a
// missing or unreachable server leaves the local-first experience untouched.
public sealed class AuthModule : IModuleActivation
{
    private readonly AuthService _service;
    private readonly IAuthApi _api;
    private readonly ISettingsCore _settings;
    private readonly IModuleExecutionGate _gate;
    private IDisposable? _settingsRegistration;

    public AuthModule(AuthDependencies dependencies)
    {
        _settings = dependencies.Settings;
        _gate = dependencies.Gate;
        _service = new(dependencies.Transport, dependencies.Secrets, dependencies.Settings, dependencies.Clock, dependencies.Diagnostics);
        _api = new GuardedAuthApi(_service, dependencies.Gate, dependencies.Diagnostics);
    }

    public static ModuleManifest Definition { get; } = new(
        "auth-core",
        [new("auth.api", 1)],
        [new("auth.transport", 1), new("auth.secrets", 1), new("settings.api", 1), new("startup.installation-state", 1), new("settings.repository", 1)],
        "Owns account session state, credential lifecycle and the account settings source.");

    public ModuleManifest Manifest => Definition;

    public IAuthApi Api => _api;

    public static SettingInitialization InitialAccountChoice(bool existingInstallation) =>
        new("auth.account-choice-v1", AuthSettingsSource.AccountChoiceMade, existingInstallation);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _settingsRegistration ??= _settings.Register(new AuthSettingsSource(_service));
        await _service.ActivateAsync(cancellationToken).ConfigureAwait(false);
        _gate.Activate();
    }

    public async Task StopAsync()
    {
        var result = await _settings.PrepareSourceStopAsync("auth-core").ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        _settingsRegistration?.Dispose();
        _settingsRegistration = null;
        await _service.DeactivateAsync().ConfigureAwait(false);
        await _gate.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
