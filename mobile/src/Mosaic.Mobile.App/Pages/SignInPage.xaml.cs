using Microsoft.Extensions.DependencyInjection;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class SignInPage : ContentPage
{
    private readonly IServiceProvider _services;

    public SignInPage(SignInViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        BindingContext = viewModel;

        viewModel.SignedIn += async (_, _) => await CloseToTasksAsync();
        viewModel.RegisterRequested += async (_, _) => await PushModalAsync<RegisterPage>();
        viewModel.ForgotPasswordRequested += async (_, _) => await PushModalAsync<ForgotPasswordPage>();
        viewModel.DeviceFlowRequested += async (_, _) => await PushModalAsync<DeviceSignInPage>();
        viewModel.OfflineRequested += async (_, _) => await CloseToTasksAsync();
    }

    private async Task PushModalAsync<TPage>()
        where TPage : Page
    {
        var page = _services.GetRequiredService<TPage>();
        await Navigation.PushModalAsync(new NavigationPage(page));
    }

    private async Task CloseToTasksAsync()
    {
        while (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync(animated: false);
        }

        try
        {
            await Shell.Current.GoToAsync("//tasks");
        }
        catch (InvalidOperationException)
        {
            // Shell is not ready in some lifecycle states; the tabs remain.
        }
    }
}
