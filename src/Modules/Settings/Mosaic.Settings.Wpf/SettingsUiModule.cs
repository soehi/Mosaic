using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;
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
        return ValueTask.CompletedTask;
    }
}
