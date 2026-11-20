using Mosaic.Presentation.Contracts;

namespace Mosaic.Maintenance.Wpf.Internal;

internal sealed class MaintenanceViewFactory(MaintenanceUiDependencies dependencies) : IViewFactory
{
    public ViewPage Create()
    {
        var view = new Views.MaintenanceView();
        var model = new MaintenanceViewModel(
            dependencies.Relocation,
            dependencies.Maintenance,
            dependencies.Lifecycle,
            dependencies.Dialogs,
            dependencies.Diagnostics);
        view.DataContext = model;
        return new ViewPage(view, model);
    }
}
