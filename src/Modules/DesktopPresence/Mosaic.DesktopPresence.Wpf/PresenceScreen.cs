using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mosaic.DesktopPresence.Wpf;

internal static class PresenceScreen
{
    // 驻留窗口几何每秒会被轮询式重算多次；工作区与 DPI 只在显示器配置变化时变，缓存掉
    // MonitorFromWindow + GetMonitorInfo + GetDpiForMonitor 这三次 P/Invoke 与 MonitorInfo 分配。
    private static readonly Dictionary<(nint Monitor, bool LastWindow), Rect> Areas = [];
    private static readonly Dictionary<(nint Monitor, bool LastWindow), double> Scales = [];

    /// <summary>Drops the cached monitor geometry; call on display, DPI or work-area changes.</summary>
    internal static void Reset()
    {
        Areas.Clear();
        Scales.Clear();
    }

    internal static Rect WorkArea(nint mainWindow, bool lastWindow)
    {
        var monitor = MonitorFromWindow(lastWindow ? mainWindow : 0, lastWindow ? 2u : 1u);
        if (Areas.TryGetValue((monitor, lastWindow), out var cached)) return cached;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var scale = ScaleOf(monitor, lastWindow);
        var area = new Rect(info.Work.Left / scale, info.Work.Top / scale,
            (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
        Areas[(monitor, lastWindow)] = area;
        return area;
    }

    private static double ScaleOf(nint monitor, bool lastWindow)
    {
        if (Scales.TryGetValue((monitor, lastWindow), out var cached)) return cached;
        var dpi = 96u;
        try { if (GetDpiForMonitor(monitor, 0, out var horizontal, out _) == 0) dpi = horizontal; }
        catch (DllNotFoundException) { }
        var scale = dpi / 96d;
        Scales[(monitor, lastWindow)] = scale;
        return scale;
    }

    // Apply the target monitor's physical coordinates, including negative origins and mixed DPI.
    internal static void Place(Window window, nint mainWindow, bool lastWindow)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var monitor = MonitorFromWindow(lastWindow ? mainWindow : 0, lastWindow ? 2u : 1u);
        var scale = ScaleOf(monitor, lastWindow);
        var x = (int)Math.Round(window.Left * scale);
        var y = (int)Math.Round(window.Top * scale);
        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        // 已经在目标像素矩形上就跳过 SetWindowPos：无谓的写入会让分层窗口每帧重建合成表面，是拖动时的闪屏来源之一。
        if (GetWindowRect(handle, out var current) && current.Left == x && current.Top == y
            && current.Right - current.Left == width && current.Bottom - current.Top == height) return;
        if (!SetWindowPos(handle, 0, x, y, width, height, 0x0014))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool PointerInside(Window window)
    {
        if (!window.IsVisible || !GetCursorPos(out var cursor)) return false;
        var point = window.PointFromScreen(new Point(cursor.X, cursor.Y));
        return point.X >= -8 && point.X <= window.ActualWidth + 8 && point.Y >= -8 && point.Y <= window.ActualHeight + 8;
    }
    internal static bool LeftPressed => (GetAsyncKeyState(1) & 0x8000) != 0;
    internal static uint DoubleClickTime => GetDoubleClickTime();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint horizontal, out uint vertical);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
