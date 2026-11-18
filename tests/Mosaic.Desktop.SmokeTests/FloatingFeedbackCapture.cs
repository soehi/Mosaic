using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    private static uint ReadDesktopPixel(int x, int y)
    {
        var dc = GetDC(0);
        Assert.NotEqual(0, dc);
        try
        {
            var pixel = GetPixel(dc, x, y);
            Assert.NotEqual(uint.MaxValue, pixel);
            return pixel;
        }
        finally { ReleaseDC(0, dc); }
    }

    private static void CaptureFloatingFeedback(NativeRect bounds)
    {
        var dc = GetDC(0);
        Assert.NotEqual(0, dc);
        var memory = CreateCompatibleDC(dc);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var bitmap = CreateCompatibleBitmap(dc, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(memory, 0, 0, width, height, dc, bounds.Left, bounds.Top, 0x40CC0020));
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            var directory = Path.Combine(Root, "artifacts/ui-preview");
            Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, "floating-pin-released.png"));
            encoder.Save(stream);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, dc);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint graphic);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint graphic);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int width, int height, nint src, int srcX, int srcY, uint operation);
}
