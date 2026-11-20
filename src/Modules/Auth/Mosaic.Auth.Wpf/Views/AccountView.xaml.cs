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
    }

    public Task InitializeAsync() => Model?.InitializeAsync() ?? Task.CompletedTask;
}
