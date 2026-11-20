using System.Windows;
using System.Windows.Controls;

namespace Mosaic.Maintenance.Wpf.Views;

public partial class MaintenanceView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MaintenanceView),
        new PropertyMetadata(false, (owner, _) => ((MaintenanceView)owner).ApplyCompactLayout()));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    public MaintenanceView()
    {
        InitializeComponent();
        // Below this width the path box and its browse button no longer fit on one row comfortably.
        SizeChanged += (_, e) => IsCompact = e.NewSize.Width < 620;
    }

    private void ApplyCompactLayout()
    {
        if (BrowseButton is null) return;
        var compact = IsCompact;
        Grid.SetRow(BrowseButton, compact ? 1 : 0);
        Grid.SetColumn(BrowseButton, compact ? 0 : 1);
        BrowseButton.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        BrowseButton.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(10, 0, 0, 0);
    }
}
