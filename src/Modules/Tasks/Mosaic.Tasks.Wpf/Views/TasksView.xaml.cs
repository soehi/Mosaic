using System.Windows;
using System.Windows.Controls;

namespace Mosaic.Tasks.Wpf.Views;

public partial class TasksView : UserControl
{
    private const double CompactBreakpoint = 920;
    private bool? _isCompact;

    public TasksView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateResponsiveLayout(ActualWidth);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var useCompactLayout = width < CompactBreakpoint;
        if (_isCompact == useCompactLayout)
        {
            return;
        }

        _isCompact = useCompactLayout;
        if (useCompactLayout)
        {
            ApplyCompactLayout();
        }
        else
        {
            ApplyWideLayout();
        }
    }

    private void ApplyWideLayout()
    {
        LayoutRoot.Margin = new Thickness(32, 30, 28, 28);
        HeaderContentGapRow.Height = new GridLength(22);
        HeaderTextColumn.Width = new GridLength(1, GridUnitType.Star);
        HeaderActionColumn.Width = GridLength.Auto;
        HeaderActionRow.Height = new GridLength(0);
        Grid.SetColumn(NewTaskButton, 1);
        Grid.SetRow(NewTaskButton, 0);
        NewTaskButton.HorizontalAlignment = HorizontalAlignment.Right;
        NewTaskButton.Margin = new Thickness(24, 0, 0, 0);

        TaskListColumn.Width = new GridLength(5, GridUnitType.Star);
        TaskListColumn.MinWidth = 300;
        ContentGapColumn.Width = new GridLength(24);
        TaskEditorColumn.Width = new GridLength(3, GridUnitType.Star);
        TaskListRow.Height = new GridLength(1, GridUnitType.Star);
        ContentGapRow.Height = new GridLength(0);
        TaskEditorRow.Height = new GridLength(0);
        Grid.SetColumn(TaskListPane, 0);
        Grid.SetRow(TaskListPane, 0);
        Grid.SetColumn(TaskEditorPane, 2);
        Grid.SetRow(TaskEditorPane, 0);
    }

    private void ApplyCompactLayout()
    {
        LayoutRoot.Margin = new Thickness(18, 22, 18, 20);
        HeaderContentGapRow.Height = new GridLength(18);
        HeaderTextColumn.Width = new GridLength(1, GridUnitType.Star);
        HeaderActionColumn.Width = new GridLength(0);
        HeaderActionRow.Height = GridLength.Auto;
        Grid.SetColumn(NewTaskButton, 0);
        Grid.SetRow(NewTaskButton, 1);
        NewTaskButton.HorizontalAlignment = HorizontalAlignment.Left;
        NewTaskButton.Margin = new Thickness(0, 18, 0, 0);

        TaskListColumn.Width = new GridLength(1, GridUnitType.Star);
        TaskListColumn.MinWidth = 0;
        ContentGapColumn.Width = new GridLength(0);
        TaskEditorColumn.Width = new GridLength(0);
        TaskListRow.Height = new GridLength(2, GridUnitType.Star);
        ContentGapRow.Height = new GridLength(20);
        TaskEditorRow.Height = new GridLength(3, GridUnitType.Star);
        Grid.SetColumn(TaskListPane, 0);
        Grid.SetRow(TaskListPane, 0);
        Grid.SetColumn(TaskEditorPane, 0);
        Grid.SetRow(TaskEditorPane, 2);
    }
}
