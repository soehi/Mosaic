using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mosaic.FloatingWindows.Wpf;

internal sealed class FloatingWindowInput : IDisposable
{
    private readonly Window _window;
    private readonly Border _frame;
    private readonly FrameworkElement _tools;
    private readonly FrameworkElement? _header;
    private readonly Func<double> _band;
    private readonly ToolTip _tip = new();
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private HwndSource? _source;
    private bool _disposed;

    internal FloatingWindowInput(Window window, Border frame, FrameworkElement tools, FrameworkElement? header, Func<double> band)
    {
        _window = window; _frame = frame; _tools = tools; _header = header; _band = band;
        _tip.SetResourceReference(ContentControl.ContentProperty, "Floating.Move");
        _hover.Tick += (_, _) => { _hover.Stop(); if (!_disposed && _window.IsEnabled) _tip.IsOpen = true; };
        window.SourceInitialized += Attach;
    }

    private void Attach(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(Hook);
    }

    private Rect Bounds(FrameworkElement element) => element.TransformToAncestor(_window).TransformBounds(new Rect(element.RenderSize));
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
            if (element.IsVisible && element is ButtonBase or TextBoxBase or ScrollBar or Thumb && Bounds(element).Contains(point)) return 1;
        if (point.Y <= rect.Top + _band()) return 2;
        const double edge = 5;
        var left = point.X < rect.Left + edge;
        var right = point.X > rect.Right - edge;
        if (point.Y > rect.Bottom - edge) return left ? 16 : right ? 17 : 15;
        if (left) return 10;
        if (right) return 11;
        if (_header is { IsVisible: true } && point.Y <= Bounds(_header).Bottom) return 2;
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
        _disposed = true; CancelHint(); _window.SourceInitialized -= Attach; _source?.RemoveHook(Hook); _source = null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public int Size, Flags; public nint Window; public int HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse tracking);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int name);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
}
