using System.Windows.Controls;
using Mosaic.Settings.Wpf.Internal;

namespace Mosaic.Settings.Wpf.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    public SettingsView()
    {
        InitializeComponent();
    }

    public async Task InitializeAsync() => await (Model?.InitializeAsync() ?? Task.CompletedTask);
}
