using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Wpf.Internal;

namespace Mosaic.Settings.Wpf;

public sealed record SettingsUiDependencies(
    ISettingsApi Api,
    IUiDispatcher Dispatcher,
    IUserDialogService Dialogs,
    IDiagnosticSink Diagnostics);

public sealed class SettingsUiModule(SettingsUiDependencies dependencies) : IModuleActivation, IUiModule
{
    private bool _active;
    private IDisposable? _localizationRegistration;

    public static ModuleManifest Definition { get; } = new(
        "settings-ui",
        [],
        [new("settings.api", 1)],
        "Owns settings navigation, page and staged value editing.");
    public ModuleManifest Manifest => Definition;
    public IReadOnlyList<NavigationItem> Navigation => _active
        ? [new("settings.navigation", "设置", "settings", "settings.home", 900)]
        : [];
    public IReadOnlyList<ViewContribution> Views => _active
        ? [new("settings.home", new SettingsViewFactory(dependencies))]
        : [];
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= Mosaic.Presentation.Wpf.MosaicLocalizationManager.Register(
            "settings-ui",
            language => Mosaic.Settings.Wpf.Internal.SettingsLocalizationDictionaries.Dictionary(language == Mosaic.Presentation.Wpf.MosaicLanguage.English));
        _active = true;
        return Task.CompletedTask;
    }
    public Task StopAsync()
    {
        _active = false;
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        _active = false;
        _localizationRegistration?.Dispose();
        _localizationRegistration = null;
        return ValueTask.CompletedTask;
    }
}
