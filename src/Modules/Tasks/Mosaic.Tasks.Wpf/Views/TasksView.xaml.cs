using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Tasks.Wpf.Views;

public partial class TasksView : UserControl
{
    private bool? _isCompact;
    private TasksViewModel? _model;
    private bool _syncingSelection;

    public TasksView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        TaskListBox.PreviewKeyDown += OnTaskListPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateResponsiveLayout(ActualWidth);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged previous)
            previous.PropertyChanged -= OnModelPropertyChanged;
        if (e.OldValue is TasksViewModel oldModel)
            oldModel.SelectedTasks.CollectionChanged -= OnSelectedTasksChanged;

        _model = e.NewValue as TasksViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            _model.SelectedTasks.CollectionChanged += OnSelectedTasksChanged;
        }

        SyncListBoxSelection();
        UpdatePaneVisibility();
        UpdateListMessageToast();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.IsEditorOpen) or nameof(TasksViewModel.HasMultiSelection))
            UpdatePaneVisibility();
        else if (e.PropertyName == nameof(TasksViewModel.SelectedItem))
            SyncListBoxSelection();
        else if (e.PropertyName == nameof(TasksViewModel.ShowListMessage))
            UpdateListMessageToast();
        else if (e.PropertyName is nameof(TasksViewModel.SelectionSummary)
            or nameof(TasksViewModel.SelectionArchiveLabel) or nameof(TasksViewModel.SelectionDeleteLabel))
            UpdateMultiSelectPaneText();
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

    // Ctrl 加选 / Alt 减选：拦截带修饰键的点击，手动维护多选集合，
    // 不走默认的单选路径（多选期间任务详情区改由多选操作面板占用）。
    private void OnTaskListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var add = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var remove = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        if (add == remove || _model is null) return;
        if (FindItem(e.OriginalSource as DependencyObject) is not { } container
            || container.DataContext is not TaskListItemViewModel item) return;
        if (_model.ToggleSelection(item, add)) e.Handled = true;
    }

    // 空白处 Esc 退出多选（点击已选中项本身已由 WPF 默认行为完成减选）。
    private void OnTaskListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _model?.HasMultiSelection == true)
        {
            _model.SelectedTasks.Clear();
            e.Handled = true;
        }
    }

    private static ListBoxItem? FindItem(DependencyObject? node)
    {
        while (node is not null && node is not ListBoxItem)
            node = VisualTreeHelper.GetParent(node);
        return node as ListBoxItem;
    }

    // SelectionMode=Multiple 下，把 ListBox.SelectedItems 的增减（普通点击、无键点击已选项）
    // 同步进 ViewModel.SelectedTasks；反向同步由 OnSelectedTasksChanged 完成，_syncingSelection 防回环。
    private void OnTaskListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _model is null) return;
        _syncingSelection = true;
        try
        {
            foreach (var item in e.AddedItems.OfType<TaskListItemViewModel>())
                if (!_model.SelectedTasks.Contains(item)) _model.SelectedTasks.Add(item);
            foreach (var item in e.RemovedItems.OfType<TaskListItemViewModel>())
                _model.SelectedTasks.Remove(item);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnSelectedTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (var item in e.NewItems.OfType<TaskListItemViewModel>())
                    if (!TaskListBox.SelectedItems.Contains(item)) TaskListBox.SelectedItems.Add(item);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
            {
                foreach (var item in e.OldItems.OfType<TaskListItemViewModel>())
                    TaskListBox.SelectedItems.Remove(item);
            }
            else
            {
                SyncListBoxSelection();
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncListBoxSelection()
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            var selected = _model?.SelectedTasks;
            for (var index = TaskListBox.SelectedItems.Count - 1; index >= 0; index--)
            {
                if (TaskListBox.SelectedItems[index] is TaskListItemViewModel item
                    && (selected is null || !selected.Contains(item)))
                    TaskListBox.SelectedItems.RemoveAt(index);
            }
            if (selected is not null)
            {
                foreach (var item in selected)
                    if (!TaskListBox.SelectedItems.Contains(item)) TaskListBox.SelectedItems.Add(item);
            }
            if (_model is not null && !ReferenceEquals(TaskListBox.SelectedItem, _model.SelectedItem))
                TaskListBox.SelectedItem = _model.SelectedItem;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void UpdateMultiSelectPaneText()
    {
        if (_model is null) return;
        MultiSelectSummary.Text = _model.SelectionSummary;
        MultiSelectArchiveButton.Content = _model.SelectionArchiveLabel;
        MultiSelectDeleteButton.Content = _model.SelectionDeleteLabel;
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
        Grid.SetColumn(MultiSelectPane, 2);
        Grid.SetRow(MultiSelectPane, 0);
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
        Grid.SetColumn(MultiSelectPane, 0);
        Grid.SetRow(MultiSelectPane, 2);
        UpdatePaneVisibility();
    }

    private void UpdatePaneVisibility()
    {
        var compact = _isCompact == true;
        var multiSelectActive = _model?.HasMultiSelection == true;
        var editorOpen = compact && _model?.IsEditorOpen == true && !multiSelectActive;
        var multiSelect = compact && multiSelectActive;
        var paneOpen = editorOpen || multiSelect;
        TaskListPane.Visibility = !paneOpen ? Visibility.Visible : Visibility.Collapsed;
        TaskEditorPane.Visibility = !multiSelectActive && (!compact || editorOpen) ? Visibility.Visible : Visibility.Collapsed;
        MultiSelectPane.Visibility = multiSelectActive && (!compact || multiSelect)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HeaderPanel.Visibility = !paneOpen ? Visibility.Visible : Visibility.Collapsed;
        HeaderContentGapRow.Height = paneOpen
            ? new GridLength(0)
            : compact ? new GridLength(16) : new GridLength(22);
        TaskListRow.Height = compact && paneOpen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TaskEditorRow.Height = compact && paneOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TaskEditorPane.SetCompactNavigationVisible(editorOpen);
        if (multiSelectActive) UpdateMultiSelectPaneText();
    }
}
