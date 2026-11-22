using System.IO;
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && CanManageAttachments()
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AttachmentDropZone_DragLeave(object sender, DragEventArgs e)
    {
        // 当前拖放区域没有需要还原的临时视觉状态。
    }

    private async void AttachmentDropZone_Drop(object sender, DragEventArgs e)
    {
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

    private async void AttachmentItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragAttachment is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _attachmentDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _attachmentDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dto = _dragAttachment;
        _dragAttachment = null;

        if (!File.Exists(dto.StoredPath)) return;

        // 资源管理器从临时文件接收导出内容；每个附件用独立子目录避免同名互相覆盖。
        var tempDir = Path.Combine(Path.GetTempPath(), "MosaicAttachments", dto.Id.ToString("D"));
        try
        {
            Directory.CreateDirectory(tempDir);
            File.Copy(dto.StoredPath, Path.Combine(tempDir, dto.FileName), overwrite: true);
        }
        catch { return; }

        var data = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(tempDir, dto.FileName) });
        var result = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);

        try { Directory.Delete(tempDir, recursive: true); } catch { }

        if (result == DragDropEffects.Move)
        {
            // 外部已完成移动：删除应用内原件与数据库记录，不再 Export（避免在临时目录留下重复副本）。
            var vm = EditorViewModel;
            if (vm is not null)
                await vm.RemoveAttachmentAsync(dto.Id);
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
