using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class RegisterPage : ContentPage
{
    private readonly RegisterViewModel _viewModel;

    public RegisterPage(RegisterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        viewModel.Registered += async (_, _) => await CloseToTasksAsync();
        viewModel.BackRequested += async (_, _) => await Navigation.PopModalAsync();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.LoadPolicyAsync();
        }
        catch (Exception)
        {
        }
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
