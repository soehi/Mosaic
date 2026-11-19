using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Sync.Contracts;
using Mosaic.Auth.Wpf.Internal;

namespace Mosaic.Auth.Wpf;

public sealed record AuthUiDependencies(
    IAuthApi Auth,
    ISyncApi Sync,
    ISettingsApi Settings,
    IUiDispatcher Dispatcher,
    IUserDialogService Dialogs,
    IDiagnosticSink Diagnostics);

/// <summary>
/// Contributes the account page without a navigation entry: the entry points are the status-bar
/// contribution (bottom-right and the sidebar's bottom area) and the shell's first-run redirect.
/// The shell never branches on authentication: signing in is optional and lives inside this module.
/// </summary>
public sealed class AuthUiModule(AuthUiDependencies dependencies) : IModuleActivation, IUiModule
{
    private bool _active;
    private IDisposable? _localizationRegistration;
    private AccountStatusItem? _statusItem;

    public static ModuleManifest Definition { get; } = new(
        "auth-ui",
        [],
        [new("auth.api", 1), new("sync.api", 1), new("settings.api", 1)],
        "Owns the account page: optional sign-in, registration, browser sign-in and backups.");

    public ModuleManifest Manifest => Definition;

    // No left-rail navigation item: account management lives in the sidebar's bottom status area.
    public IReadOnlyList<NavigationItem> Navigation => [];

    public IReadOnlyList<ViewContribution> Views => _active
        ? [new("account.home", new AuthViewFactory(dependencies))]
        : [];

    public IReadOnlyList<IStatusContribution> Status => _active && _statusItem is not null ? [_statusItem] : [];

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= MosaicLocalizationManager.Register(
            "auth-ui",
            language => AuthLocalizationDictionaries.Dictionary(language == MosaicLanguage.English));
        _statusItem ??= new AccountStatusItem(
            dependencies.Auth, dependencies.Settings, dependencies.Dialogs,
            dependencies.Dispatcher, dependencies.Diagnostics);
        _active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _active = false;
        _statusItem?.Dispose();
        _statusItem = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _active = false;
        _localizationRegistration?.Dispose();
        _localizationRegistration = null;
        _statusItem?.Dispose();
        _statusItem = null;
        return ValueTask.CompletedTask;
    }
}
