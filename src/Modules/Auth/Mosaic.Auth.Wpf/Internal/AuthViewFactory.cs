using Mosaic.Presentation.Contracts;

namespace Mosaic.Auth.Wpf.Internal;

internal sealed class AuthViewFactory(AuthUiDependencies dependencies) : IViewFactory
{
    // The page view model is created per visit and disposed by the settings host. The offline
    // choice is a page-level preference, so the factory (module lifetime) owns the bit.
    private bool _offlineChosen;

    public ViewPage Create()
    {
        var view = new Views.AccountView();
        var model = new AccountViewModel(
            dependencies.Auth,
            dependencies.Sync,
            dependencies.Settings,
            dependencies.Dialogs,
            dependencies.Dispatcher,
            dependencies.Diagnostics,
            offlineChosen: _offlineChosen,
            offlineChosenChanged: value => _offlineChosen = value);
        view.DataContext = model;
        return new ViewPage(view, model);
    }
}
