using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Tasks.Wpf.Views;

public partial class TasksView : UserControl
{
    private bool? _isCompact;
    private TasksViewModel? _model;

    public TasksView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateResponsiveLayout(ActualWidth);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged previous)
            previous.PropertyChanged -= OnModelPropertyChanged;

        _model = e.NewValue as TasksViewModel;
        if (_model is not null)
            _model.PropertyChanged += OnModelPropertyChanged;

        _isCompact = null;
        UpdateResponsiveLayout(ActualWidth);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.CompactBreakpoint) or
            nameof(TasksViewModel.ListMinColumnWidth) or nameof(TasksViewModel.EditorPaneWidth) or
            nameof(TasksViewModel.PaneGap))
        {
            _isCompact = null;
            UpdateResponsiveLayout(ActualWidth);
        }
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var breakpoint = _model?.CompactBreakpoint ?? 920;
        var useCompactLayout = width < breakpoint;
        if (_isCompact != useCompactLayout)
        {
            _isCompact = useCompactLayout;
            ApplyHeaderLayout(useCompactLayout);
        }
        UpdatePaneWidths(width);
    }

    private void ApplyHeaderLayout(bool compact)
    {
        LayoutRoot.Margin = compact ? new Thickness(18, 16, 18, 16) : new Thickness(32, 30, 28, 28);
        HeaderContentGapRow.Height = new GridLength(compact ? 18 : 22);
        HeaderTextColumn.Width = new GridLength(1, GridUnitType.Star);
        HeaderActionColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        HeaderActionRow.Height = compact ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(NewTaskButton, compact ? 0 : 1);
        Grid.SetRow(NewTaskButton, compact ? 1 : 0);
        NewTaskButton.HorizontalAlignment = HorizontalAlignment.Right;
        NewTaskButton.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(24, 0, 0, 0);
    }

    private void UpdatePaneWidths(double width)
    {
        var available = Math.Max(0, width - LayoutRoot.Margin.Left - LayoutRoot.Margin.Right);
        var listMinimum = _model?.ListMinColumnWidth ?? 300;
        var editorWidth = Math.Clamp(_model?.EditorPaneWidth ?? 480, 360, 720);
        var gap = Math.Clamp(_model?.PaneGap ?? 24, 0, 64);
        var deficit = Math.Max(0, listMinimum + editorWidth + gap - available);

        // Keep both panes usable by giving up empty space before shrinking the editor.
        var gapReduction = Math.Min(gap, deficit);
        gap -= gapReduction;
        deficit -= gapReduction;
        editorWidth -= Math.Min(editorWidth - 360, deficit);
        editorWidth = Math.Min(editorWidth, available);

        TaskListColumn.Width = new GridLength(1, GridUnitType.Star);
        TaskListColumn.MinWidth = Math.Min(listMinimum, Math.Max(0, available - editorWidth - gap));
        ContentGapColumn.Width = new GridLength(gap);
        TaskEditorColumn.Width = new GridLength(editorWidth);
    }
}
