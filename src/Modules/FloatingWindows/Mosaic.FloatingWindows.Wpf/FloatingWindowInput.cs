using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mosaic.Presentation.Contracts;

namespace Mosaic.FloatingWindows.Wpf;

internal sealed class FloatingWindowInput : IDisposable
{
    private readonly Window _window;
    private readonly Border _frame;
    private readonly FrameworkElement _tools;
    private readonly FrameworkElement? _header;
    private readonly Func<double> _band;
    private readonly FrameworkElement? _content;
    private readonly Func<Point, FloatingContentDragKind>? _contentDrag;
    private readonly ToolTip _tip = new();
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private HwndSource? _source;
    private FrameworkElement? _pressButton;
    private bool _pressContent;
    private Point _pressPoint;
    private bool _suppressClick;
    private bool _disposed;

    internal FloatingWindowInput(Window window, Border frame, FrameworkElement tools, FrameworkElement? header, Func<double> band,
        FrameworkElement? content = null, Func<Point, FloatingContentDragKind>? contentDrag = null)
    {
        _window = window; _frame = frame; _tools = tools; _header = header; _band = band; _content = content; _contentDrag = contentDrag;
        _tip.SetResourceReference(ContentControl.ContentProperty, "Floating.Move");
        _hover.Tick += (_, _) => { _hover.Stop(); if (!_disposed && _window.IsEnabled) _tip.IsOpen = true; };
        window.SourceInitialized += Attach;
        window.PreviewMouseLeftButtonDown += OnMouseDown;
        window.PreviewMouseMove += OnMouseMove;
        window.PreviewMouseLeftButtonUp += OnMouseUp;
    }

    // Buttons stay HTCLIENT so clicks keep working; a WPF threshold gesture turns a press-and-drag into a real move.
    internal bool CanStartHeaderDrag(object? source) => HeaderButton(source) is not null;
    private FrameworkElement? HeaderButton(object? source)
    {
        if (_disposed || !_window.IsEnabled || source is not DependencyObject node) return null;
        var button = Ancestor<ButtonBase>(node);
        if (button is null || !button.IsVisible || !button.IsEnabled) return null;
        return Contains(_tools, button) || (_header is { IsVisible: true } header && Contains(header, button)) ? button : null;
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _suppressClick = false; _pressButton = null; _pressContent = false;
        if (e.ChangedButton != MouseButton.Left) return;
        _pressButton = HeaderButton(e.OriginalSource);
        if (_pressButton is not null)
        {
            _pressPoint = e.GetPosition(_window);
            return;
        }
        // Deferred marks read-only text: the press stays interactive (caret/selection) and only a
        // press-and-drag beyond the system threshold starts the native caption move loop.
        var point = e.GetPosition(_window);
        if (_contentDrag is not null && _content is { IsVisible: true } &&
            _contentDrag(point) == FloatingContentDragKind.Deferred)
            _pressContent = true;
        _pressPoint = point;
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if ((_pressButton is null && !_pressContent) || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_window);
        if (Math.Abs(point.X - _pressPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _pressPoint.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
        _pressButton = null; _pressContent = false; _suppressClick = true; CancelHint();
        // Reuse the native caption move loop: Window.DragMove() posts SC_MOVE, which ends at once for a
        // press that started on a captured child button; releasing capture and asking for HTCAPTION does not.
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(_window).Handle, 0x00A1, 2, 0);
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressButton = null; _pressContent = false;
        if (_suppressClick) e.Handled = true;
    }
    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = Parent(current)) if (current is T match) return match;
        return null;
    }
    private static bool Contains(DependencyObject ancestor, DependencyObject node)
    {
        for (var current = node; current is not null; current = Parent(current)) if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static DependencyObject? Parent(DependencyObject node)
    {
        try { return node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        catch (InvalidOperationException) { return LogicalTreeHelper.GetParent(node); }
    }

    private void Attach(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(Hook);
    }

    private Rect Bounds(FrameworkElement element) => element.TransformToAncestor(_window).TransformBounds(new Rect(element.RenderSize));
    private Point MouseToElement(Point point, FrameworkElement element)
    {
        try { return element.TransformToAncestor(_window).Inverse.Transform(point); }
        catch (InvalidOperationException) { return point; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    internal int HitTest(Point point)
    {
        if (_disposed || !_window.IsEnabled) return 1;
        var rect = Bounds(_frame);
        if (!rect.Contains(point)) return -1;
        const double radius = 12;
        var cx = Math.Clamp(point.X, rect.Left + radius, rect.Right - radius);
        var cy = Math.Clamp(point.Y, rect.Top + radius, rect.Bottom - radius);
        if ((point.X-cx)*(point.X-cx)+(point.Y-cy)*(point.Y-cy) > radius*radius) return -1;
        if (Bounds(_tools).Contains(point)) return 1;
        foreach (var element in Descendants(_frame).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || !(element is ButtonBase or TextBoxBase or ScrollBar or Thumb) || !Bounds(element).Contains(point)) continue;
            // A read-only text box that the content declares draggable blanks through: glyphs keep
            // HTCLIENT (selection/copy), blank inside it returns HTCAPTION via the content query.
            if (element is TextBoxBase && _contentDrag is not null &&
                _contentDrag(element is FrameworkElement fe ? MouseToElement(point, fe) : point) == FloatingContentDragKind.Immediate)
                continue;
            return 1;
        }
        if (point.Y <= rect.Top + _band()) return 2;
        const double edge = 5;
        var left = point.X < rect.Left + edge;
        var right = point.X > rect.Right - edge;
        if (point.Y > rect.Bottom - edge) return left ? 16 : right ? 17 : 15;
        if (left) return 10;
        if (right) return 11;
        if (_header is { IsVisible: true } && point.Y <= Bounds(_header).Bottom) return 2;
        // Content blanks (footer gaps, empty regions, off-glyph notes space) drag the window.
        if (_contentDrag is not null && _content is { IsVisible: true } &&
            _contentDrag(MouseToElement(point, _content)) == FloatingContentDragKind.Immediate) return 2;
        return 1;
    }

    private nint Hook(nint hwnd, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == 0x0084)
        {
            var point = _window.PointFromScreen(new Point(unchecked((short)(long)lp), unchecked((short)((long)lp >> 16))));
            handled = true; return HitTest(point);
        }
        if (message == 0x0020)
        {
            var hit = unchecked((short)(long)lp);
            var cursor = hit switch { 2 => 32646, 10 or 11 => 32644, 15 => 32645, 16 => 32643, 17 => 32642, _ => 0 };
            if (cursor != 0) { SetCursor(LoadCursor(0, cursor)); handled = true; return 1; }
        }
        if (message == 0x00A3 && wp == 2) { handled = true; return 0; }
        if (message == 0x00A0 && wp == 2 && _window.IsEnabled)
        {
            if (!_hover.IsEnabled && !_tip.IsOpen) _hover.Start();
            var tracking = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = hwnd };
            TrackMouseEvent(ref tracking);
        }
        else if (message is 0x00A0 or 0x02A2 or 0x0200 or 0x0231 or 0x001F or 0x0215) CancelHint();
        return 0;
    }

    internal void CancelHint() { _hover.Stop(); _tip.IsOpen = false; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelHint(); _window.SourceInitialized -= Attach;
        _window.PreviewMouseLeftButtonDown -= OnMouseDown; _window.PreviewMouseMove -= OnMouseMove; _window.PreviewMouseLeftButtonUp -= OnMouseUp;
        _source?.RemoveHook(Hook); _source = null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public int Size, Flags; public nint Window; public int HoverTime; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse tracking);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int name);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
}
