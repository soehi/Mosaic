using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Tasks.Wpf;

public sealed record TasksUiDependencies(
    ITasksApi Api,
    IUiDispatcher Dispatcher,
    IUserDialogService Dialogs,
    IDiagnosticSink Diagnostics,
    Mosaic.Settings.Contracts.ISettingsCore? Settings = null);

public sealed class TasksUiModule(TasksUiDependencies dependencies) : IModuleActivation, IUiModule
{
    private bool _active;
    private IDisposable? _localizationRegistration;
    public static ModuleManifest Definition { get; } = new(
        "tasks-ui",
        [],
        [new("tasks.api", 1)],
        "Owns task navigation, views and view models.");
    public ModuleManifest Manifest => Definition;
    public IReadOnlyList<NavigationItem> Navigation => _active ? [new("tasks.navigation", "任务", "check-square", "tasks.list", 100)] : [];
    public IReadOnlyList<ViewContribution> Views => _active ? [new("tasks.list", new TaskViewFactory(dependencies))] : [];
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= Mosaic.Presentation.Wpf.MosaicLocalizationManager.Register(
            "tasks-ui",
            language => Mosaic.Tasks.Wpf.Internal.TaskLocalization.Dictionary(language == Mosaic.Presentation.Wpf.MosaicLanguage.English));
        _active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _active = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _active = false;
        _localizationRegistration?.Dispose();
        _localizationRegistration = null;
        await ValueTask.CompletedTask;
    }
}
