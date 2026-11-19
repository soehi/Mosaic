using System.Windows;
using System.Windows.Controls;
using Mosaic.Auth.Wpf.Internal;

namespace Mosaic.Auth.Wpf.Views;

public partial class AccountView : UserControl
{
    private AccountViewModel? Model => DataContext as AccountViewModel;

    public AccountView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public Task InitializeAsync() => Model?.InitializeAsync() ?? Task.CompletedTask;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AccountViewModel previous) previous.PasswordsCleared -= OnPasswordsCleared;
        if (e.NewValue is AccountViewModel model) model.PasswordsCleared += OnPasswordsCleared;
    }

    private void OnPasswordsCleared(object? sender, EventArgs e)
    {
        // Secrets never round-trip through bindings: the view is the only place holding them.
        foreach (var box in new[] { LoginPassword, RegisterPassword, RegisterConfirm, ResetPassword, ResetConfirm })
            box.Password = string.Empty;
    }

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.Password = LoginPassword.Password;
    }

    private void OnRegisterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.Password = RegisterPassword.Password;
    }

    private void OnRegisterConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.ConfirmPassword = RegisterConfirm.Password;
    }

    private void OnResetPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.NewPassword = ResetPassword.Password;
    }

    private void OnResetConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.ConfirmNewPassword = ResetConfirm.Password;
    }
}
