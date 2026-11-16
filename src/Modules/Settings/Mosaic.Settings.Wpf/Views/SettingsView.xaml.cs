using System.Windows;
using System.Windows.Controls;
using Mosaic.Settings.Wpf.Internal;

namespace Mosaic.Settings.Wpf.Views;

public partial class SettingsView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(SettingsView), new PropertyMetadata(false));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }
    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => IsCompact = e.NewSize.Width < 900;
    }

    public async Task InitializeAsync() => await (Model?.InitializeAsync() ?? Task.CompletedTask);
}
