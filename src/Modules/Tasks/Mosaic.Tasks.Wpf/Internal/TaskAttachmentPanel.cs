using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

// 速览/浮窗共用的附件区：类型徽标 + 文件名 + 大小，拖出即复制导出（不删除任务内附件）。
// 拖出必须在 UI 线程同步调用 DoDragDrop，否则 OLE 事件路由抛跨线程异常（0.18.0 崩溃根因）。
internal sealed class TaskAttachmentPanel : StackPanel
{
    private readonly ITaskAttachmentApi? _api;
    private Point _dragStart;
    private TaskAttachmentDto? _drag;

    internal TaskAttachmentPanel(ITaskAttachmentApi? api)
    {
        _api = api;
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
                ToolTip = TaskLocalization.Localize("Tasks.Attachments.DragHint", "拖出以复制导出")
            };
            row.SetResourceReference(BackgroundProperty, MosaicTheme.SurfaceBrushKey);
            row.SetResourceReference(Border.BorderBrushProperty, MosaicTheme.BorderBrushKey);
            row.BorderThickness = new Thickness(1);
            var name = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = attachment.FileName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0)
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
            var size = new TextBlock
            {
                FontSize = 11,
                Text = TaskAttachmentVisuals.SizeLabel(attachment.SizeBytes),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            size.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextMutedBrushKey);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = TaskAttachmentVisuals.CreateIcon(attachment.FileName);
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
            Grid.SetColumn(name, 1);
            content.Children.Add(name);
            Grid.SetColumn(size, 2);
            content.Children.Add(size);
            AutomationProperties.SetName(content, $"{attachment.FileName} · {TaskAttachmentVisuals.SizeLabel(attachment.SizeBytes)}");
            row.Child = content;
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
        if (_api is null) return;
        _ = StartDragAsync((DependencyObject)sender, dto);
    }

    // 暂存复制在后台完成，DoDragDrop 回到 UI 线程同步调用（0.18.0 崩溃根因）。
    private async Task StartDragAsync(DependencyObject source, TaskAttachmentDto dto)
    {
        if (_api is null) return;
        var staged = await TaskAttachmentDrag.StageAsync(
            ct => _api.OpenReadAsync(dto.TaskId, dto.Id, ct), dto.Id, dto.FileName).ConfigureAwait(true);
        if (!staged.IsSuccess) return;
        try
        {
            TaskAttachmentDrag.Run(source, TaskAttachmentDrag.CreateData(dto, staged.Value));
        }
        finally
        {
            TaskAttachmentDrag.Release(staged.Value.Directory);
        }
    }
}