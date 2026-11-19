using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;
    private readonly StartupService _startup;

    public SettingsPage(SettingsViewModel viewModel, StartupService startup)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _startup = startup;
        BindingContext = viewModel;
        _viewModel.SetVersion(AppInfo.Current.VersionString);
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
}
