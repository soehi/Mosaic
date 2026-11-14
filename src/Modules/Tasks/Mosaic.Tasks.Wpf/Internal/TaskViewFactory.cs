using Mosaic.Presentation.Contracts;
using Mosaic.Tasks.Wpf.Views;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskViewFactory(TasksUiDependencies dependencies) : IViewFactory
{
    public ViewPage Create()
    {
        if (!dependencies.Dispatcher.CheckAccess()) throw new InvalidOperationException("Views must be created on the UI thread.");
        var model = new TasksViewModel(dependencies.Api, dependencies.Dispatcher, dependencies.Dialogs, dependencies.Diagnostics);
        return new(new TasksView { DataContext = model }, model);
    }
}
