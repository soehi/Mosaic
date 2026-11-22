using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

// 速览/浮窗共用的附件区：文件名与大小列表，支持拖出（Copy / Shift+Move）。
// 拖出必须在 UI 线程同步调用 DoDragDrop，否则 OLE 事件路由抛跨线程异常（0.18.0 崩溃根因）。
internal sealed class TaskAttachmentPanel : StackPanel
{
    private readonly ITaskAttachmentTransferRegistry _registry;
    private Point _dragStart;
    private TaskAttachmentDto? _drag;

    // Move 成功后的回调（由宿主编排删除附件与刷新）；None/Copy 由组件自行清理暂存。
    internal Func<TaskAttachmentDragTransfer, Task>? MoveCompleted { get; set; }

    internal TaskAttachmentPanel(ITaskAttachmentTransferRegistry registry)
    {
        _registry = registry;
        Orientation = Orientation.Vertical;
    }

    internal void Update(IReadOnlyList<TaskAttachmentDto> attachments)
    {
        Children.Clear();
        if (attachments.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        foreach (var attachment in attachments)
        {
            var row = new Border
            {
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 0, 3),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.SizeAll,
                Tag = attachment,
                ToolTip = TaskLocalization.Localize("Tasks.Attachments.DragHint", "拖出以导出（Shift = 移动）")
            };
            row.SetResourceReference(BackgroundProperty, MosaicTheme.SurfaceBrushKey);
            row.SetResourceReference(Border.BorderBrushProperty, MosaicTheme.BorderBrushKey);
            row.BorderThickness = new Thickness(1);
            var text = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"📎 {attachment.FileName} · {FormatSize(attachment.SizeBytes)}"
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
            AutomationProperties.SetName(row, text.Text);
            row.Child = text;
            row.PreviewMouseLeftButtonDown += OnMouseDown;
            row.PreviewMouseMove += OnMouseMove;
            Children.Add(row);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _drag = sender is Border { Tag: TaskAttachmentDto dto } ? dto : null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _drag is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var dto = _drag;
        _drag = null;
        if (!File.Exists(dto.StoredPath)) return;
        var tempDir = Path.Combine(Path.GetTempPath(), "MosaicAttachments", dto.Id.ToString("D"));
        try
        {
            Directory.CreateDirectory(tempDir);
            File.Copy(dto.StoredPath, Path.Combine(tempDir, dto.FileName), overwrite: true);
        }
        catch { return; }

        var transfer = new TaskAttachmentDragTransfer(dto.TaskId, dto.Id, tempDir, ExpectedRevision: 0);
        _registry.Put(transfer);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { Path.Combine(tempDir, dto.FileName) });
        data.SetData(TaskAttachmentDragTransfer.FormatName, transfer);
        var result = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        if (result == DragDropEffects.Move && MoveCompleted is { } completed)
        {
            // 暂存副本已被外部接收方消耗，原件删除由宿主处理。
            _ = completed(transfer);
        }
        else
        {
            _registry.Discard(dto.Id);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d),
        >= 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes)
    };
}
