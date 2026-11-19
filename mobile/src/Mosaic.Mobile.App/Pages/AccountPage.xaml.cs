using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class AccountPage : ContentPage
{
    private readonly AccountViewModel _viewModel;
    private readonly StartupService _startup;
    private readonly IServiceProvider _services;

    public AccountPage(AccountViewModel viewModel, StartupService startup, IServiceProvider services)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _startup = startup;
        _services = services;
        BindingContext = viewModel;
        _viewModel.SignInRequested += OnSignInRequested;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _startup.InitializeAsync();
            await _viewModel.LoadAsync();
        }
        catch (Exception)
        {
        }
    }

    private async void OnSignInRequested(object? sender, EventArgs e)
    {
        var page = _services.GetRequiredService<SignInPage>();
        await Navigation.PushModalAsync(new NavigationPage(page));
    }

    private async void OnAuthorizeDesktopClicked(object? sender, EventArgs e)
    {
        var page = _services.GetRequiredService<DeviceAuthorizePage>();
        await Navigation.PushModalAsync(new NavigationPage(page));
    }
}
