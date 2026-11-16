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
    private IDisposable? _settingsRegistration;
    private readonly TaskUiSettingsTarget _settingsTarget = new();
    public static ModuleManifest Definition { get; } = new(
        "tasks-ui",
        [new("presence.tasks-view", 1)],
        [new("tasks.api", 1)],
        "Owns task navigation, views and view models.");
    public ModuleManifest Manifest => Definition;
    public ICompanionViewFactory CompanionFactory => new TaskPeekViewFactory(dependencies, () => _active);
    public IReadOnlyList<NavigationItem> Navigation => _active
        ? [new("tasks.navigation", "任务", "check-square", "tasks.list", 100, "Tasks.Navigation")]
        : [];
    public IReadOnlyList<ViewContribution> Views => _active ? [new("tasks.list", new TaskViewFactory(dependencies, _settingsTarget))] : [];
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= Mosaic.Presentation.Wpf.MosaicLocalizationManager.Register(
            "tasks-ui",
            language => Mosaic.Tasks.Wpf.Internal.TaskLocalization.Dictionary(language == Mosaic.Presentation.Wpf.MosaicLanguage.English));
        _settingsRegistration ??= dependencies.Settings?.Register(new TaskUiSettingSource(dependencies.Dispatcher, _settingsTarget));
        _active = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (dependencies.Settings is not null && _settingsRegistration is not null)
        {
            var result = await dependencies.Settings.PrepareSourceStopAsync("tasks-ui");
            if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        }
        _active = false;
        _settingsRegistration?.Dispose();
        _settingsRegistration = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _localizationRegistration?.Dispose();
        _localizationRegistration = null;
        await ValueTask.CompletedTask;
    }
}
