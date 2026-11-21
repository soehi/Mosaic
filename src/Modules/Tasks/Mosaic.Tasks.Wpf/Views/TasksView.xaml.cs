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
        PreviewKeyDown += OnTasksViewPreviewKeyDown;
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
        {
            // 多选进出时 SelectionChanged 可能先于 HasMultiSelection 通知到达，
            // 必须按最新状态重放同步（如多选退出后把 SelectedItem 锚点写回）。
            UpdatePaneVisibility();
            if (e.PropertyName == nameof(TasksViewModel.HasMultiSelection))
                SyncListBoxSelection();
        }
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

    // 任务项点击统一走预览事件手动裁决：SelectionMode=Extended 只为让容器 IsSelected
    // 可多行并存（0.17.11 用 Single 时多选高亮互清），原生 Ctrl/Shift 组合手势被
    // e.Handled 拦截不会触发；多选集合托管的选中项不经 SelectedItem，
    // 见 TasksViewModel.HasMultiSelection。按住 Ctrl 切换多选、否则单选。
    private void OnTaskListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model is null) return;
        if (FindItem(e.OriginalSource as DependencyObject) is not { } container
            || container.DataContext is not TaskListItemViewModel item) return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            _model.ToggleSelection(item);
        else
            _model.SelectedItem = item;
    }

    // Esc 退出多选挂在视图级：焦点在编辑器或多选面板按钮上时也要能收到。
    private void OnTasksViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _model?.HasMultiSelection == true)
        {
            _model.ClearMultiSelection();
            e.Handled = true;
        }
    }

    private static ListBoxItem? FindItem(DependencyObject? node)
    {
        while (node is not null && node is not ListBoxItem)
            node = VisualTreeHelper.GetParent(node);
        return node as ListBoxItem;
    }

    // 点击已在预览事件手动裁决（Ctrl 切换多选、否则单选），SelectionChanged 只来自
    // SyncListBoxSelection 的容器 IsSelected 调整与键鼠导航兜底；均不再回写
    // ViewModel.SelectedTasks，否则单选会经镜像误入多选集合（0.17.10 的架构纠缠）。
    private void OnTaskListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    // 容器循环只负责多选叠加高亮，锚点行的 IsSelected 由 SelectedItem 驱动：
    // 多选期间锚点为 null、循环按 SelectedTasks 施加；单选期间清掉多选残留高亮，
    // 对锚点行既不设也不清，交给 ListBox 原生选中驱动（异步完成，同步写回会抖动）。
    private void SyncListBoxSelection()
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            // 只能经 SelectedItem/容器 IsSelected 调整选中（不直接改 SelectedItems 集合）：
            // 先把锚点对齐到 VM 的 SelectedItem，多选期间锚点为 null。
            // 注意必须用 Extended 模式：Single 模式下容器 IsSelected 是独占的，
            // 异步生效时会互清（0.17.11 的 Ctrl 加选高亮只剩最后一项）。
            var anchor = _model?.HasMultiSelection == true ? null : _model?.SelectedItem;
            if (!ReferenceEquals(TaskListBox.SelectedItem, anchor))
                TaskListBox.SelectedItem = anchor;
            var selected = _model?.SelectedTasks;
            if (selected is { Count: > 0 })
            {
                // 多选集合是独立的选中态来源，逐项用容器 IsSelected 叠加/移除高亮；
                // 高亮通知经 ListBoxItem 属性变化异步生效，触发 SelectionChanged 前仍受 _syncingSelection 保护。
                foreach (var item in TaskListBox.Items.OfType<TaskListItemViewModel>())
                {
                    if (TaskListBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                        continue;
                    var shouldSelect = selected.Contains(item);
                    if (container.IsSelected != shouldSelect)
                        container.IsSelected = shouldSelect;
                }
            }
            else
            {
                // 单选时清空多选残留；锚点行交由 ListBox 原生选中驱动，循环不触碰。
                foreach (var item in TaskListBox.Items.OfType<TaskListItemViewModel>())
                {
                    if (ReferenceEquals(item, anchor)) continue;
                    if (TaskListBox.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container
                        && container.IsSelected)
                        container.IsSelected = false;
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnSelectedTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncListBoxSelection();

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
