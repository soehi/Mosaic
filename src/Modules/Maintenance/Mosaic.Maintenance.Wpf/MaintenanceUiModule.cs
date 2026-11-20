using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;
using Mosaic.Maintenance.Wpf.Internal;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Maintenance.Wpf;

public sealed record MaintenanceUiDependencies(
    IStorageRelocation Relocation,
    IMaintenanceService Maintenance,
    IApplicationLifecycle Lifecycle,
    IUserDialogService Dialogs,
    IDiagnosticSink Diagnostics,
    SettingsPageRegistry? SettingsPages = null);

/// <summary>Contributes the maintenance page: storage relocation, repair and uninstall.</summary>
public sealed class MaintenanceUiModule(MaintenanceUiDependencies dependencies) : IModuleActivation, IUiModule
{
    private IDisposable? _localizationRegistration;
    private IDisposable? _pageRegistration;

    public static ModuleManifest Definition { get; } = new(
        "maintenance-ui",
        [new("app.maintenance", 1)],
        [new("storage.relocation", 1), new("settings.api", 1)],
        "Owns the maintenance page: database relocation, MSI repair and uninstall.");

    public ModuleManifest Manifest => Definition;

    public IReadOnlyList<NavigationItem> Navigation => [];
    public IReadOnlyList<ViewContribution> Views => [];
    public IReadOnlyList<IStatusContribution> Status => [];

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= MosaicLocalizationManager.Register(
            "maintenance-ui",
            language => MaintenanceLocalizationDictionaries.Dictionary(language == MosaicLanguage.English));
        _pageRegistration ??= dependencies.SettingsPages?.Register(
            new SettingsPageContribution(
                nameof(SettingsGroup.Maintenance),
                "maintenance.settings",
                new MaintenanceViewFactory(dependencies)));
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
