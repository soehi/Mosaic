using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
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
        UpdateListMessageToast();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TasksViewModel.IsEditorOpen))
            UpdatePaneVisibility();
        else if (e.PropertyName == nameof(TasksViewModel.ShowListMessage))
            UpdateListMessageToast();
        else if (e.PropertyName is nameof(TasksViewModel.CompactBreakpoint)
            or nameof(TasksViewModel.EditorPaneWidth) or nameof(TasksViewModel.PaneGap)
            or nameof(TasksViewModel.ListMinColumnWidth))
        {
            _isCompact = null;
            UpdateResponsiveLayout(ActualWidth);
        }
        else if (e.PropertyName == nameof(TasksViewModel.ListMinColumnWidth))
        {
            if (_isCompact == false) TaskListColumn.MinWidth = _model?.ListMinColumnWidth ?? 300;
        }
    }

    // 归档/恢复/删除等结果提示（ListMessageToast）不再占据 Grid 行参与布局，
    // 而是悬浮在列表右下角；出现时淡入、ShowListMessage 变 false 时淡出，
    // 全程不改变任务列表的几何位置，避免列表回弹导致的点击错位。
    private static readonly Duration ToastFadeInDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration ToastFadeOutDuration = new(TimeSpan.FromMilliseconds(220));
    private string _lastListMessage = "";

    private void UpdateListMessageToast()
    {
        var show = _model?.ShowListMessage == true;
        ListMessageToast.BeginAnimation(OpacityProperty, null);
        if (show)
        {
            // 淡出期间 Message 可能已被清空，显示最近一次非空文本，避免空浮层闪现。
            if (!string.IsNullOrEmpty(_model?.Message)) _lastListMessage = _model.Message;
            ListMessageText.Text = _lastListMessage;
            ListMessageToast.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, ToastFadeInDuration);
            ListMessageToast.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            var fadeOut = new DoubleAnimation(1, 0, ToastFadeOutDuration);
            fadeOut.Completed += (_, _) =>
            {
                if (_model?.ShowListMessage != true)
                    ListMessageToast.Visibility = Visibility.Collapsed;
            };
            ListMessageToast.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var breakpoint = _model?.CompactBreakpoint ?? 920;
        var useCompactLayout = width < breakpoint;
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

        TaskListColumn.Width = new GridLength(1, GridUnitType.Star);
        TaskListColumn.MinWidth = _model?.ListMinColumnWidth ?? 300;
        ContentGapColumn.Width = new GridLength(_model?.PaneGap ?? 24);
        TaskEditorColumn.Width = new GridLength(Math.Clamp(
            ActualWidth - LayoutRoot.Margin.Left - LayoutRoot.Margin.Right -
            (_model?.PaneGap ?? 24) - (_model?.ListMinColumnWidth ?? 300),
            360, _model?.EditorPaneWidth ?? 480));
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
        HeaderPanel.Visibility = Visibility.Visible;
        HeaderContentGapRow.Height = new GridLength(16);
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
        ContentGapRow.Height = new GridLength(0);
        Grid.SetColumn(TaskListPane, 0);
        Grid.SetRow(TaskListPane, 0);
        Grid.SetColumn(TaskEditorPane, 0);
        Grid.SetRow(TaskEditorPane, 2);
        UpdatePaneVisibility();
    }

    private void UpdatePaneVisibility()
    {
        var compact = _isCompact == true;
        var editorOpen = compact && _model?.IsEditorOpen == true;
        TaskListPane.Visibility = !editorOpen ? Visibility.Visible : Visibility.Collapsed;
        TaskEditorPane.Visibility = !compact || editorOpen ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = !editorOpen ? Visibility.Visible : Visibility.Collapsed;
        HeaderContentGapRow.Height = editorOpen
            ? new GridLength(0)
            : compact ? new GridLength(16) : new GridLength(22);
        TaskListRow.Height = compact && editorOpen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TaskEditorRow.Height = compact && editorOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TaskEditorPane.SetCompactNavigationVisible(editorOpen);
    }
}
