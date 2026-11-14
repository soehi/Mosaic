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

        UpdatePaneVisibility();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TasksViewModel.IsEditorOpen))
            UpdatePaneVisibility();
        else if (e.PropertyName == nameof(TasksViewModel.CompactBreakpoint))
        {
            _isCompact = null;
            UpdateResponsiveLayout(ActualWidth);
        }
        else if (e.PropertyName == nameof(TasksViewModel.ListMinColumnWidth))
        {
            if (_isCompact == false) TaskListColumn.MinWidth = _model?.ListMinColumnWidth ?? 300;
        }
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var breakpoint = _model?.CompactBreakpoint ?? 920;
        var useCompactLayout = width < breakpoint;
        if (_isCompact == useCompactLayout) return;

        _isCompact = useCompactLayout;
        if (useCompactLayout)
            ApplyCompactLayout();
        else
            ApplyWideLayout();
    }

    private void ApplyWideLayout()
    {
        LayoutRoot.Margin = new Thickness(32, 30, 28, 28);
        HeaderPanel.Visibility = Visibility.Visible;
        HeaderContentGapRow.Height = new GridLength(22);
        HeaderTextColumn.Width = new GridLength(1, GridUnitType.Star);
        HeaderActionColumn.Width = GridLength.Auto;
        HeaderActionRow.Height = new GridLength(0);
        Grid.SetColumn(NewTaskButton, 1);
        Grid.SetRow(NewTaskButton, 0);
        NewTaskButton.HorizontalAlignment = HorizontalAlignment.Right;
        NewTaskButton.Margin = new Thickness(24, 0, 0, 0);

        TaskListColumn.Width = new GridLength(5, GridUnitType.Star);
        TaskListColumn.MinWidth = _model?.ListMinColumnWidth ?? 300;
        ContentGapColumn.Width = new GridLength(24);
        TaskEditorColumn.Width = new GridLength(3, GridUnitType.Star);
        TaskListRow.Height = new GridLength(1, GridUnitType.Star);
        ContentGapRow.Height = new GridLength(0);
        TaskEditorRow.Height = new GridLength(0);
        Grid.SetColumn(TaskListPane, 0);
        Grid.SetRow(TaskListPane, 0);
        Grid.SetColumn(TaskEditorPane, 2);
        Grid.SetRow(TaskEditorPane, 0);
        TaskEditorPane.SetCompactNavigationVisible(false);
        UpdatePaneVisibility();
    }

    private void ApplyCompactLayout()
    {
        LayoutRoot.Margin = new Thickness(18, 16, 18, 16);
        HeaderTextColumn.Width = new GridLength(1, GridUnitType.Star);
        HeaderActionColumn.Width = new GridLength(0);
        HeaderActionRow.Height = GridLength.Auto;
        Grid.SetColumn(NewTaskButton, 0);
        Grid.SetRow(NewTaskButton, 1);
        NewTaskButton.HorizontalAlignment = HorizontalAlignment.Left;
        NewTaskButton.Margin = new Thickness(0, 16, 0, 0);

        TaskListColumn.Width = new GridLength(1, GridUnitType.Star);
        TaskListColumn.MinWidth = 0;
        ContentGapColumn.Width = new GridLength(0);
        TaskEditorColumn.Width = new GridLength(0);
        TaskListRow.Height = new GridLength(1, GridUnitType.Star);
        ContentGapRow.Height = new GridLength(0);
        TaskEditorRow.Height = new GridLength(0);
        Grid.SetColumn(TaskListPane, 0);
        Grid.SetRow(TaskListPane, 0);
        Grid.SetColumn(TaskEditorPane, 0);
        Grid.SetRow(TaskEditorPane, 0);
        TaskEditorPane.SetCompactNavigationVisible(true);
        UpdatePaneVisibility();
    }

    private void UpdatePaneVisibility()
    {
        if (_isCompact != true)
        {
            TaskListPane.Visibility = Visibility.Visible;
            TaskEditorPane.Visibility = Visibility.Visible;
            HeaderPanel.Visibility = Visibility.Visible;
            return;
        }

        var showEditor = _model?.IsEditorOpen == true;
        HeaderPanel.Visibility = showEditor ? Visibility.Collapsed : Visibility.Visible;
        HeaderContentGapRow.Height = showEditor ? new GridLength(0) : new GridLength(18);
        TaskListPane.Visibility = showEditor ? Visibility.Collapsed : Visibility.Visible;
        TaskEditorPane.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
    }
}
