using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class DeviceAuthorizePage : ContentPage
{
    public DeviceAuthorizePage(DeviceAuthorizeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnCloseClicked(object? sender, EventArgs e) =>
        await Navigation.PopModalAsync();
}
