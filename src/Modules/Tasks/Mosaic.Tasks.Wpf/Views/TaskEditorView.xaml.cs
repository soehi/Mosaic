using System.Windows.Controls;
using System.Windows;

namespace Mosaic.Tasks.Wpf.Views;

public partial class TaskEditorView : UserControl
{
    private const double CompactActionsWidth = 480;
    private const double CompactActionsHeight = 680;
    private bool? _compactActions;

    public TaskEditorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    internal void SetCompactNavigationVisible(bool visible) =>
        CompactNavigationBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        UpdateResponsiveActions(ActualWidth, ActualHeight);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveActions(e.NewSize.Width, e.NewSize.Height);

    private void UpdateResponsiveActions(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        var useCompactActions = width < CompactActionsWidth || height < CompactActionsHeight;
        if (_compactActions == useCompactActions) return;
        _compactActions = useCompactActions;

        if (useCompactActions)
        {
            EditorActionSecondRow.Height = GridLength.Auto;
            Grid.SetRow(ToggleButton, 0);
            Grid.SetColumn(ToggleButton, 0);
            Grid.SetColumnSpan(ToggleButton, 1);
            Grid.SetRow(ReloadButton, 0);
            Grid.SetColumn(ReloadButton, 2);
            Grid.SetColumnSpan(ReloadButton, 1);
            Grid.SetRow(DeleteButton, 1);
            Grid.SetColumn(DeleteButton, 0);
            Grid.SetColumnSpan(DeleteButton, 5);
            DeleteButton.Margin = new Thickness(0, 7, 0, 0);
            FormScrollViewer.Padding = new Thickness(18, 14, 10, 10);
            EditorActionBar.Padding = new Thickness(14, 10, 14, 10);
            return;
        }

        EditorActionSecondRow.Height = new GridLength(0);
        Grid.SetRow(ToggleButton, 0);
        Grid.SetColumn(ToggleButton, 0);
        Grid.SetColumnSpan(ToggleButton, 1);
        Grid.SetRow(ReloadButton, 0);
        Grid.SetColumn(ReloadButton, 2);
        Grid.SetColumnSpan(ReloadButton, 1);
        Grid.SetRow(DeleteButton, 0);
        Grid.SetColumn(DeleteButton, 4);
        Grid.SetColumnSpan(DeleteButton, 1);
        DeleteButton.Margin = new Thickness(0);
        FormScrollViewer.Padding = new Thickness(22, 20, 14, 12);
        EditorActionBar.Padding = new Thickness(14, 12, 14, 12);
    }
}
