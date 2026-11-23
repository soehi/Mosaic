using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Tasks.Wpf.Views;

public partial class TaskEditorView : UserControl
{
    private bool? _compactActions;
    private TasksViewModel? _model;

    public TaskEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    internal void SetCompactNavigationVisible(bool visible) =>
        CompactNavigationBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        UpdateResponsiveActions(ActualWidth, ActualHeight);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveActions(e.NewSize.Width, e.NewSize.Height);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged previous) previous.PropertyChanged -= OnModelPropertyChanged;
        _model = e.NewValue as TasksViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        ApplyModelSettings();
        _compactActions = null;
        UpdateResponsiveActions(ActualWidth, ActualHeight);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.CompactActionsWidth) or nameof(TasksViewModel.CompactActionsHeight) or
            nameof(TasksViewModel.NotesMinHeight) or nameof(TasksViewModel.TaskButtonMinWidth) or nameof(TasksViewModel.TaskButtonMinHeight))
        {
            ApplyModelSettings();
            _compactActions = null;
            UpdateResponsiveActions(ActualWidth, ActualHeight);
        }
    }

    private void ApplyModelSettings()
    {
        NotesTextBox.MinHeight = _model?.NotesMinHeight ?? 128;
        var minimumWidth = _model?.TaskButtonMinWidth ?? 96;
        var minimumHeight = _model?.TaskButtonMinHeight ?? 40;
        foreach (var button in new[] { ToggleButton, ReloadButton, DeleteButton })
        {
            button.MinWidth = minimumWidth;
            button.MinHeight = minimumHeight;
        }
    }

    private TaskEditorViewModel? EditorViewModel => (DataContext as TasksViewModel)?.Editor;

    private bool CanManageAttachments() => EditorViewModel?.CanManageAttachments == true;

    private void AttachmentDropZone_DragOver(object sender, DragEventArgs e)
    {
        // 应用内附件拖拽只用于导出复制；只有外部文件才允许落进附件区，避免把自家附件再添加一份。
        var external = !e.Data.GetDataPresent(TaskAttachmentDrag.InternalFormatName)
            && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = external && CanManageAttachments() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AttachmentDropZone_DragLeave(object sender, DragEventArgs e)
    {
        // 当前拖放区域没有需要还原的临时视觉状态。
    }

    private async void AttachmentDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TaskAttachmentDrag.InternalFormatName)) return;
        if (!CanManageAttachments() || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var vm = EditorViewModel;
        if (vm is null) return;
        await vm.AddAttachmentsAsync(files);
    }

    private void AttachmentDropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (!CanManageAttachments()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择附件" };
        if (dlg.ShowDialog() != true) return;
        var vm = EditorViewModel;
        if (vm is null) return;
        _ = vm.AddAttachmentsAsync(dlg.FileNames);
    }

    private async void AttachmentRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var vm = EditorViewModel;
        if (vm is null) return;
        await vm.RemoveAttachmentAsync(id);
    }

    private Point _attachmentDragStart;
    private TaskAttachmentDto? _dragAttachment;

    private void AttachmentItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _attachmentDragStart = e.GetPosition(null);
        _dragAttachment = sender is Border { DataContext: TaskAttachmentDto dto } ? dto : null;
    }

    // 必须在 UI 线程同步调用：DoDragDrop 内部构造事件路由，转到后台线程会抛出
    // 「调用线程无法访问此对象」并使应用退出（0.18.0 崩溃根因）。
    // 暂存复制放到后台辅助方法里完成，回到 UI 线程后再同步发起 OLE 拖放。
    private void AttachmentItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragAttachment is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _attachmentDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _attachmentDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dto = _dragAttachment;
        _dragAttachment = null;
        if (EditorViewModel is null) return;
        _ = StartAttachmentDragAsync((DependencyObject)sender, dto);
    }

    private async Task StartAttachmentDragAsync(DependencyObject source, TaskAttachmentDto dto)
    {
        var vm = EditorViewModel;
        if (vm is null) return;

        var staged = await TaskAttachmentDrag
            .StageAsync(ct => vm.OpenAttachmentAsync(dto.Id, ct), dto.Id, dto.FileName)
            .ConfigureAwait(true);
        if (!staged.IsSuccess)
        {
            var error = staged.Error!;
            await vm.DispatcherInvoke(() => vm.ReportAttachmentFailure(error));
            return;
        }

        try
        {
            // 只提供 Copy：拖出等价于导出副本，任务内附件与其记录保持不变。
            TaskAttachmentDrag.Run(source, TaskAttachmentDrag.CreateData(dto, staged.Value));
        }
        finally
        {
            TaskAttachmentDrag.Release(staged.Value.Directory);
        }
    }

    private void UpdateResponsiveActions(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        var useCompactActions = width < (_model?.CompactActionsWidth ?? 480) || height < (_model?.CompactActionsHeight ?? 680);
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
