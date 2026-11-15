using Mosaic.Presentation.Contracts;

namespace Mosaic.Settings.Wpf.Internal;

internal sealed class SettingsViewFactory(SettingsUiDependencies dependencies) : IViewFactory
{
    public ViewPage Create()
    {
        var view = new Views.SettingsView();
        var model = new SettingsViewModel(dependencies.Api, dependencies.Dialogs);
        view.DataContext = model;
        return new ViewPage(view, model);
    }
}
