using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Mosaic.FloatingWindows.Contracts;
namespace Mosaic.Tasks.Wpf.Internal;

internal partial class TaskPeekView : UserControl
{
    internal event Action<TaskPeekRow>? OpenRequested;
    internal Func<TaskPeekRow, Task>? CompleteRequested;
    internal Func<Mosaic.Tasks.Contracts.TaskFilter, Task>? FilterRequested;
    private async void CompleteTask(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_suppressClick) return;
        CancelDrag();
        if (sender is Button { DataContext: TaskPeekRow row } && CompleteRequested is { } complete) await complete(row);
    }
    private async void FilterClicked(object sender, RoutedEventArgs e)
    {
        CancelDrag();
        if (sender is RadioButton { Tag: string tag } && FilterRequested is { } filter) await filter(Enum.Parse<Mosaic.Tasks.Contracts.TaskFilter>(tag));
    }
    internal Func<TaskPeekRow, FloatingPlacementHint?, CancellationToken, Task<bool>>? DetachRequested;
    internal event Action? Detached;
    internal Action<TaskPeekRow, FloatingPlacementHint>? PreviewRequested;
    internal Action<FloatingPlacementHint>? PreviewMoved;
    internal Action? PreviewEnded;
    internal Func<bool>? CanDetach;
    internal event Action<bool>? DragChanged;
    private FrameworkElement? _candidate;
    private Point _start;
    private Point _grab;
    private bool _dragging, _suppressClick, _previewing;
    private DragHintAdorner? _preview;
    private KeyEventHandler? _escapeHandler;
    private CancellationTokenSource? _opening;
    internal TaskPeekView() { InitializeComponent(); }
    private void OpenTask(object sender, RoutedEventArgs e)
    {
        if (_suppressClick) return;
        if (sender is Button { DataContext: TaskPeekRow row }) OpenRequested?.Invoke(row);
    }
    private void StartGesture(object sender, MouseButtonEventArgs e)
    {
        if (_opening is not null) { e.Handled = true; return; }
        CancelDrag(); _suppressClick = false;
        if (CanDetach?.Invoke() != true || sender is not FrameworkElement surface) return;
        _candidate = surface; _start = e.GetPosition(this); _grab = e.GetPosition(surface);
    }
    private void MoveGesture(object sender, MouseEventArgs e)
    {
        if (_candidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (!_dragging && (Math.Abs(point.X - _start.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - _start.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            ActivateDrag(point);
        }
        if (_dragging) { e.Handled = true; _preview?.Update(point); MovePreview(point); AdornerLayer.GetAdornerLayer(this)?.InvalidateArrange(); }
    }
    internal void ActivateDrag(Point position)
    {
        if (_candidate is not { } surface) return;
        if (!surface.CaptureMouse()) { _candidate = null; return; }
        _dragging = true; _suppressClick = true; DragChanged?.Invoke(true);
        _preview = new DragHintAdorner(this, TaskLocalization.Localize("Tasks.Floating.Drag", "松手以创建小窗 · Esc取消"), surface);
        AdornerLayer.GetAdornerLayer(this)?.Add(_preview);
        _escapeHandler = (_, args) => { if (args.Key == Key.Escape) CancelDrag(); };
        Window.GetWindow(this)?.PreviewKeyDown += _escapeHandler;
        _preview.Update(position);
        AdornerLayer.GetAdornerLayer(this)?.InvalidateArrange();
        StartPreview(position);
    }
    private void StartPreview(Point position)
    {
        if (PreviewRequested is not { } preview || _candidate?.DataContext is not TaskPeekRow row || Hint(position) is not { } hint) return;
        _previewing = true; preview(row, hint);
    }
    private void MovePreview(Point position)
    {
        if (!_previewing || PreviewMoved is not { } moved || Hint(position) is not { } hint) return;
        moved(hint);
    }
    private void EndPreview()
    {
        if (!_previewing) return;
        _previewing = false; PreviewEnded?.Invoke();
    }
    private FloatingPlacementHint? Hint(Point viewPoint)
    {
        if (PresentationSource.FromVisual(this) is null) return null;
        var screen = PointToScreen(viewPoint);
        return new FloatingPlacementHint(screen.X, screen.Y, _grab.X, _grab.Y);
    }
    private async void EndGesture(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _candidate is null) { _candidate = null; return; }
        e.Handled = true;
        await FinishDrag(_candidate, e.GetPosition((UIElement?)Window.GetWindow(this) ?? this), e.GetPosition(this));
    }
    private async void CaptureLost(object sender, MouseEventArgs e)
    {
        // A child button releasing its capture during hand-off does not end the drag.
        if (!_dragging || _candidate is not { } surface || !ReferenceEquals(e.OriginalSource, surface)) return;
        if (e.LeftButton == MouseButtonState.Pressed) { CancelDrag(); return; }
        await FinishDrag(surface, e.GetPosition((UIElement?)Window.GetWindow(this) ?? this), e.GetPosition(this));
    }
    internal async Task FinishDrag(FrameworkElement surface, Point windowPosition, Point viewPosition)
    {
        if (!_dragging || !ReferenceEquals(surface, _candidate)) return;
        var window = Window.GetWindow(this);
        if (window is null || new Rect(window.RenderSize).Contains(windowPosition) ||
            surface.DataContext is not TaskPeekRow row || Hint(viewPosition) is not { } hint || DetachRequested is not { } request)
        {
            CancelDrag();
            return;
        }

        using var opening = new CancellationTokenSource();
        _opening = opening;
        ResetGesture();
        bool opened;
        try { opened = await request(row, hint, opening.Token); }
        finally
        {
            if (ReferenceEquals(_opening, opening))
            {
                _opening = null;
                DragChanged?.Invoke(false);
            }
        }
        if (opened && !opening.IsCancellationRequested) Detached?.Invoke();
    }
    internal bool CancelDrag()
    {
        var was = _dragging || _opening is not null;
        var opening = _opening; _opening = null; opening?.Cancel();
        ResetGesture();
        if (was) DragChanged?.Invoke(false);
        return was;
    }
    private void ResetGesture()
    {
        _dragging = false;
        if (_preview is not null)
        {
            AdornerLayer.GetAdornerLayer(this)?.Remove(_preview);
            _preview = null;
        }
        var host = Window.GetWindow(this);
        if (_escapeHandler is not null && host is not null) host.PreviewKeyDown -= _escapeHandler;
        _escapeHandler = null;
        var surface = _candidate; _candidate = null;
        if (surface?.IsMouseCaptured == true) surface.ReleaseMouseCapture();
        _suppressClick = false;
        EndPreview();
    }
    private void OpenMenu(object sender, ContextMenuEventArgs e)
    {
        if (CanDetach?.Invoke() != true || sender is not Button { DataContext: TaskPeekRow row } button) { e.Handled = true; return; }
        CancelDrag();
        var menu = button.ContextMenu!; menu.Items.Clear();
        var item = new MenuItem { Header = TaskLocalization.Localize("Tasks.Floating.Open", "在小窗中打开") };
        item.Click += async (_, _) => { if (DetachRequested is { } request) await request(row, null, CancellationToken.None); }; menu.Items.Add(item);
    }
}
