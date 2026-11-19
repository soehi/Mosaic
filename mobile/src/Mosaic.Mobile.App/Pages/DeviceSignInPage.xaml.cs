using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class DeviceSignInPage : ContentPage
{
    private readonly DeviceSignInViewModel _viewModel;

    public DeviceSignInPage(DeviceSignInViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        viewModel.SignedIn += async (_, _) => await CloseToTasksAsync();
        viewModel.OpenLinkRequested += async (_, uri) =>
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var target))
            {
                try
                {
                    await Launcher.Default.OpenAsync(target);
                }
                catch (Exception)
                {
                    // No browser: the user can still type the address.
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.ResumeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Pause();
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
        }
    }
}
