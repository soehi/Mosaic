using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Shell.Wpf;

/// <summary>
/// 让系统绘制的窗口边框与标题栏跟随 Mosaic 当前主题。
/// WPF 自身的 WindowStyle 无论深浅都在 Aero/DWM 里按系统浅色绘制标题栏，
/// 只能在 HWND 上设置 DWMWA_USE_IMMERSIVE_DARK_MODE。
/// 主题切换（含跟随系统明暗）时对该进程内已附加的窗口重新应用。
/// </summary>
public static class MosaicWindowChrome
{
    // 20 是 Windows 10 2004 / Build 19041（20H1）及 Windows 11 的正式属性名；
    // 19 是 Windows 10 1809–1903（Build 17763–18363）使用的旧名，20H1 起失效。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private static readonly object Gate = new();
    private static readonly List<WeakReference<Window>> Windows = [];
    private static bool _subscribed;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Closed -= OnWindowClosed;
        window.Closed += OnWindowClosed;
        window.Loaded -= OnLoaded;
        window.Loaded += OnLoaded;
        lock (Gate)
        {
            Windows.RemoveAll(reference => !reference.TryGetTarget(out _));
            Windows.Add(new WeakReference<Window>(window));
            if (!_subscribed)
            {
                MosaicThemeManager.ThemeChanged += OnThemeChanged;
                _subscribed = true;
            }
        }

        if (window.IsLoaded)
        {
            window.Loaded -= OnLoaded;
            Apply(window);
        }
    }

    public static void Detach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Closed -= OnWindowClosed;
        window.Loaded -= OnLoaded;
        lock (Gate)
        {
            Windows.RemoveAll(reference =>
                !reference.TryGetTarget(out var tracked) || ReferenceEquals(tracked, window));
            if (Windows.Count == 0 && _subscribed)
            {
                MosaicThemeManager.ThemeChanged -= OnThemeChanged;
                _subscribed = false;
            }
        }
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        Detach(window);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        window.Loaded -= OnLoaded;
        Apply(window);
    }

    private static void OnThemeChanged(object? sender, EventArgs e)
    {
        List<Window> alive;
        lock (Gate)
        {
            alive = [.. Windows.Select(reference => reference.TryGetTarget(out var window) ? window : null)
                .OfType<Window>()];
            Windows.RemoveAll(reference => !reference.TryGetTarget(out _));
        }

        foreach (var window in alive) Apply(window);
    }

    private static void Apply(Window window)
    {
        // 高对比度主题下让系统自行绘制标题栏，不参与 DWM 着色，避免覆盖系统配色。
        if (MosaicThemeManager.Current == MosaicThemeVariant.HighContrast) return;

        // ThemeChanged 可能在别的线程上触发（主题由另一条线程应用时，例如测试宿主或
        // 多 UI 线程场景）。HWND 与窗口线程绑定，跨线程直接取用会抛
        // InvalidOperationException 并打断整个主题应用，因此先切回窗口自己的调度器。
        if (!window.Dispatcher.CheckAccess())
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(window)));
            return;
        }

        // 已关闭或正在关闭的窗口：句柄可能已销毁，也不再需要着色。
        if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;

        IntPtr handle;
        try
        {
            handle = new WindowInteropHelper(window).Handle;
        }
        catch (ObjectDisposedException)
        {
            // 窗口已关闭、句柄已销毁：无需再着色。
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (handle == IntPtr.Zero) return;

        var dark = MosaicThemeManager.Current == MosaicThemeVariant.Dark ? 1 : 0;
        try
        {
            // 20H1（Build 19041）及 Windows 11 认属性 20；1809–1903（Build 17763–18363）
            // 只认旧名 19。先按新属性设置，失败再回退。
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 极少数精简系统没有 dwmapi.dll；标题栏保持系统默认即可，不影响功能。
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
