using Mosaic.Presentation.Contracts;
using Mosaic.Tasks.Wpf.Views;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskViewFactory(TasksUiDependencies dependencies, TaskUiSettingsTarget settingsTarget, int? initialFilter = null) : IViewFactory
{
    public ViewPage Create()
    {
        if (!dependencies.Dispatcher.CheckAccess()) throw new InvalidOperationException("Views must be created on the UI thread.");
        var model = new TasksViewModel(dependencies.Api, dependencies.Dispatcher, dependencies.Dialogs, dependencies.Diagnostics, dependencies.Settings, settingsTarget, dependencies.Changes);
        if (initialFilter is { } filter) model.FilterIndex = filter;
        return new(new TasksView { DataContext = model }, model);
    }
}
