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
    Mosaic.Settings.Contracts.ISettingsCore? Settings = null,
    ITaskChangeSource? Changes = null, IFloatingDetachSource? Detach = null);

public sealed class TasksUiModule(TasksUiDependencies dependencies) : IModuleActivation, IUiModule
{
    private bool _active;
    private readonly TaskDetachSource _detach=new(dependencies.Dispatcher);
    public IFloatingDetachSource DetachSource=>_detach;
    public IFloatingContentFactory FloatingFactory=>new TaskFloatingContentFactory(dependencies,_settingsTarget,()=>_active);
    private IDisposable? _localizationRegistration;
    private IDisposable? _settingsRegistration;
    private readonly TaskUiSettingsTarget _settingsTarget = new();
    public static ModuleManifest Definition { get; } = new(
        "tasks-ui",
        [new("presence.tasks-view", 1),new("floating.task-content",1),new("floating.detach-source",1)],
        [new("tasks.api", 1),new("tasks.changes",1)],
        "Owns task navigation, views and view models.");
    public ModuleManifest Manifest => Definition;
    public ICompanionViewFactory CompanionFactory => new TaskPeekViewFactory(dependencies with { Detach=_detach }, () => _active);
    public IReadOnlyList<NavigationItem> Navigation => _active
        ? [
            new("tasks.navigation", "任务", "check-square", "tasks.list", 100, "Tasks.Navigation"),
            new("tasks.archived.navigation", "已归档任务", "folder", "tasks.archived.list", 110, "Tasks.Archived.Navigation")
        ]
        : [];
    public IReadOnlyList<ViewContribution> Views => _active
        ? [
            new("tasks.list", new TaskViewFactory(dependencies, _settingsTarget)),
            new("tasks.archived.list", new TaskViewFactory(dependencies, _settingsTarget, archivedOnly: true))
        ]
        : [];
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= Mosaic.Presentation.Wpf.MosaicLocalizationManager.Register(
            "tasks-ui",
            language => Mosaic.Tasks.Wpf.Internal.TaskLocalization.Dictionary(language == Mosaic.Presentation.Wpf.MosaicLanguage.English));
        if (dependencies.Settings is { } settings && _settingsRegistration is null)
        {
            var source = new TaskUiSettingSource(dependencies.Dispatcher, _settingsTarget);
            _settingsRegistration = settings.Register(source);
            var values = await settings.GetValuesAsync(cancellationToken);
            if (values.IsSuccess) await source.ApplyAsync(values.Value, cancellationToken);
        }
        _active = true;
    }

    public async Task StopAsync()
    {
        if (dependencies.Settings is not null && _settingsRegistration is not null)
        {
            var result = await dependencies.Settings.PrepareSourceStopAsync("tasks-ui");
            if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        }
        _active = false;
        _detach.Deactivate();
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
