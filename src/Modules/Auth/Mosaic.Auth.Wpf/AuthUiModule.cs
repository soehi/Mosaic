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
    IDiagnosticSink Diagnostics,
    SettingsPageRegistry? SettingsPages = null);

/// <summary>Contributes account management to the settings page.</summary>
public sealed class AuthUiModule(AuthUiDependencies dependencies) : IModuleActivation, IUiModule
{
    private IDisposable? _localizationRegistration;
    private IDisposable? _pageRegistration;

    public static ModuleManifest Definition { get; } = new(
        "auth-ui",
        [],
        [new("auth.api", 1), new("sync.api", 1), new("settings.api", 1)],
        "Owns the account page: optional sign-in, registration, browser sign-in and backups.");

    public ModuleManifest Manifest => Definition;

    public IReadOnlyList<NavigationItem> Navigation => [];
    public IReadOnlyList<ViewContribution> Views => [];
    public IReadOnlyList<IStatusContribution> Status => [];

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= MosaicLocalizationManager.Register(
            "auth-ui",
            language => AuthLocalizationDictionaries.Dictionary(language == MosaicLanguage.English));
        _pageRegistration ??= dependencies.SettingsPages?.Register(
            new SettingsPageContribution(nameof(SettingsGroup.AccountsAndBackup), "account.settings", new AuthViewFactory(dependencies)));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _pageRegistration?.Dispose();
        _pageRegistration = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _localizationRegistration?.Dispose();
        _localizationRegistration = null;
        _pageRegistration?.Dispose();
        _pageRegistration = null;
        return ValueTask.CompletedTask;
    }
}
