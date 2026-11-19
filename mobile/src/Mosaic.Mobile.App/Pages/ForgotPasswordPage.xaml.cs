using Mosaic.Mobile.Presentation;

namespace Mosaic.Mobile.App.Pages;

public partial class ForgotPasswordPage : ContentPage
{
    public ForgotPasswordPage(ForgotPasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        viewModel.Completed += async (_, _) => await Navigation.PopModalAsync();
        viewModel.BackRequested += async (_, _) => await Navigation.PopModalAsync();
    }
}
