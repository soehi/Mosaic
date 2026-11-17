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
        CancelDrag();
        if (sender is Button { DataContext: TaskPeekRow row } && CompleteRequested is { } complete) await complete(row);
    }
    private async void FilterClicked(object sender, RoutedEventArgs e)
    {
        CancelDrag();
        if (sender is RadioButton { Tag: string tag } && FilterRequested is { } filter) await filter(Enum.Parse<Mosaic.Tasks.Contracts.TaskFilter>(tag));
    }
    internal Func<TaskPeekRow, FloatingPlacementHint?, CancellationToken, Task>? DetachRequested;
    internal Func<bool>? CanDetach;
    internal event Action<bool>? DragChanged;
    private Button? _candidate;
    private Point _start;
    private Point _grab;
    private bool _dragging, _suppressClick;
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
        CancelDrag(); _suppressClick = false;
        if (CanDetach?.Invoke() != true || sender is not Button button) return;
        _candidate = button; _start = e.GetPosition(this); _grab = e.GetPosition(button);
    }
    private void MoveGesture(object sender, MouseEventArgs e)
    {
        if (_candidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (!_dragging && (Math.Abs(point.X - _start.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - _start.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            ActivateDrag(point);
        }
        if (_dragging) { e.Handled = true; _preview?.Update(point); AdornerLayer.GetAdornerLayer(this)?.InvalidateArrange(); }
    }
    internal void ActivateDrag(Point position)
    {
        if (_candidate is null) return;
        _dragging = true; _suppressClick = true; _candidate.CaptureMouse(); DragChanged?.Invoke(true);
        _preview = new DragHintAdorner(this, TaskLocalization.Localize("Tasks.Floating.Drag", "松手以创建小窗 · Esc取消"));
        AdornerLayer.GetAdornerLayer(this)?.Add(_preview);
        _escapeHandler = (_, args) => { if (args.Key == Key.Escape) CancelDrag(); };
        Window.GetWindow(this)?.PreviewKeyDown += _escapeHandler;
        _preview.Update(position);
        AdornerLayer.GetAdornerLayer(this)?.InvalidateArrange();
    }
    private async void EndGesture(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _candidate is null) { _candidate = null; return; }
        e.Handled = true;
        var button = _candidate; _candidate = null;
        await FinishDrag(button, e.GetPosition((UIElement?)Window.GetWindow(this) ?? this), e.GetPosition(this));
    }
    private async void CaptureLost(object sender, MouseEventArgs e)
    {
        if (!_dragging || _candidate is null) return;
        var button = _candidate; _candidate = null;
        await FinishDrag(button, e.GetPosition((UIElement?)Window.GetWindow(this) ?? this), e.GetPosition(this));
    }
    internal async Task FinishDrag(Button button, Point windowPosition, Point viewPosition)
    {
        if (button.DataContext is not TaskPeekRow row) { CancelDrag(); return; }
        var window = Window.GetWindow(this);
        var outside = window is not null && (windowPosition.X < 0 || windowPosition.Y < 0 || windowPosition.X > window.ActualWidth || windowPosition.Y > window.ActualHeight);
        var opening = new CancellationTokenSource(); _opening = opening;
        var screen = window?.PointToScreen(viewPosition);
        try { if (outside && screen is not null && DetachRequested is { } request) await request(row, new(screen.Value.X, screen.Value.Y, _grab.X, _grab.Y), opening.Token); }
        finally { if (ReferenceEquals(_opening, opening)) { _opening = null; CancelDrag(); } opening.Dispose(); }
    }
    internal bool CancelDrag()
    {
        var opening = _opening; _opening = null; opening?.Cancel();
        var was = _dragging; _dragging = false;
        if (_preview is not null)
        {
            AdornerLayer.GetAdornerLayer(this)?.Remove(_preview);
            _preview = null;
        }
        var host = Window.GetWindow(this);
        if (_escapeHandler is not null && host is not null) host.PreviewKeyDown -= _escapeHandler;
        _escapeHandler = null;
        var button = _candidate; _candidate = null;
        if (button?.IsMouseCaptured == true) button.ReleaseMouseCapture();
        if (was) DragChanged?.Invoke(false);
        return was;
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
