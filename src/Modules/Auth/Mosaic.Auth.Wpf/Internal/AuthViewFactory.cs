using Mosaic.Presentation.Contracts;

namespace Mosaic.Auth.Wpf.Internal;

internal sealed class AuthViewFactory(AuthUiDependencies dependencies) : IViewFactory
{
    public ViewPage Create()
    {
        var view = new Views.AccountView();
        var model = new AccountViewModel(
            dependencies.Auth,
            dependencies.Sync,
            dependencies.Settings,
            dependencies.Dialogs,
            dependencies.Dispatcher,
            dependencies.Diagnostics);
        view.DataContext = model;
        return new ViewPage(view, model);
    }
}
