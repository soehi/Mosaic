using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    [Fact]
    public async Task Shared_buttons_release_press_feedback_and_show_keyboard_focus()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await CheckButtonFeedbackAsync(); completed.SetResult(); }
                catch (Exception exception) { completed.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task CheckButtonFeedbackAsync()
    {
        var previousDpi = SetThreadDpiAwarenessContext(-4);
        GetCursorPos(out var previousCursor);
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = new Window
        {
            Width = 320, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, Content = panel
        };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative)
        });
        var normal = new Button { Content = "Normal" };
        var primary = new Button { Content = "Primary", IsDefault = true };
        primary.SetResourceReference(FrameworkElement.StyleProperty, MosaicTheme.AccentButtonKey);
        var danger = new Button { Content = "Danger" };
        danger.SetResourceReference(FrameworkElement.StyleProperty, MosaicTheme.DangerButtonKey);
        var quiet = new Button { Content = "Quiet" };
        quiet.SetResourceReference(FrameworkElement.StyleProperty, MosaicTheme.QuietButtonKey);
        var buttons = new[] { normal, primary, danger, quiet };
        var clicks = new int[buttons.Length];
        for (var index = 0; index < buttons.Length; index++)
        {
            var slot = index;
            buttons[index].Click += (_, _) => clicks[slot]++;
            panel.Children.Add(buttons[index]);
        }

        static Border Face(Button button) => (Border)button.Template.FindName("ButtonBorder", button);
        static bool IsMarkVisible(DependencyObject node) =>
            (node is Border { Width: 16, Height: 4, Opacity: 1 }) ||
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
                .Any(index => IsMarkVisible(VisualTreeHelper.GetChild(node, index)));
        static bool HasFocusMark(Button button) =>
            AdornerLayer.GetAdornerLayer(button)?.GetAdorners(button)?.Any(IsMarkVisible) == true;
        static void MoveTo(FrameworkElement element, double x, double y)
        {
            var point = element.PointToScreen(new Point(x, y));
            Assert.True(SetCursorPos((int)point.X, (int)point.Y));
        }
        static void PressKey(ushort key)
        {
            SendKey(key, false);
            SendKey(key, true);
        }

        try
        {
            window.Show();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
            await UntilCondition(() => window.IsActive, "Test window did not activate");
            MoveTo(panel, 0, panel.ActualHeight - 1);
            for (var index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                var face = Face(button);
                var idleBorder = face.BorderBrush;
                MoveTo(button, button.ActualWidth / 2, button.ActualHeight / 2);
                await UntilCondition(() => button.IsMouseOver, "Button did not receive pointer hover");
                Assert.Equal(0.88, face.Opacity);
                SendMouse(0, 0, 2);
                await UntilCondition(() => button.IsPressed, "Button did not enter pressed state");
                Assert.Equal(0.72, face.Opacity);
                SendMouse(0, 0, 4);
                await UntilCondition(() => clicks[index] == 1, "Pointer release did not click exactly once");
                Assert.False(button.IsPressed);
                Assert.False(button.IsMouseCaptured);
                Assert.True(button.IsKeyboardFocused);
                Assert.False(HasFocusMark(button));
                Assert.Equal(0.88, face.Opacity);
                MoveTo(panel, 0, panel.ActualHeight - 1);
                await UntilCondition(() => !button.IsMouseOver, "Pointer did not leave the button");
                Assert.Equal(1, face.Opacity);
                Assert.Same(idleBorder, face.BorderBrush);
            }

            PressKey(0x09); // Tab
            await UntilCondition(() => normal.IsKeyboardFocused && HasFocusMark(normal), "Tab did not show keyboard focus");
            SendKey(0x10, false);
            PressKey(0x09);
            SendKey(0x10, true);
            await UntilCondition(() => quiet.IsKeyboardFocused && HasFocusMark(quiet), "Shift+Tab did not return keyboard focus");
            SendKey(0x20, false); // Space
            await UntilCondition(() => quiet.IsPressed, "Space did not press the focused button");
            Assert.Equal(0.72, Face(quiet).Opacity);
            SendKey(0x20, true);
            await UntilCondition(() => clicks[3] == 2, "Space release did not click exactly once");
            Assert.False(quiet.IsPressed);
            Assert.Equal(1, Face(quiet).Opacity);
            Assert.True(HasFocusMark(quiet));
            PressKey(0x0D); // Enter
            await UntilCondition(() => clicks[3] == 3 || clicks[1] == 2, "Enter did not activate the focused or default button");
            Assert.Equal(6, clicks.Sum());

            MoveTo(quiet, quiet.ActualWidth / 2, quiet.ActualHeight / 2);
            await UntilCondition(() => quiet.IsMouseOver, "Pointer did not return to the focused button");
            SendMouse(0, 0, 2);
            await UntilCondition(() => quiet.IsPressed, "Pointer did not press the focused button");
            MoveTo(panel, 0, panel.ActualHeight - 1);
            await UntilCondition(() => !quiet.IsPressed, "Dragging outside did not cancel the pressed state");
            SendMouse(0, 0, 4);
            await UntilCondition(() => !quiet.IsMouseCaptured, "Drag release did not release capture");
            Assert.Equal(6, clicks.Sum());
            Assert.False(HasFocusMark(quiet));
            Assert.Equal(1, Face(quiet).Opacity);

            quiet.IsEnabled = false;
            MoveTo(quiet, quiet.ActualWidth / 2, quiet.ActualHeight / 2);
            await Task.Delay(80);
            Assert.Equal(1, Face(quiet).Opacity);
            Assert.Equal(window.Resources[MosaicTheme.DisabledSurfaceBrushKey], Face(quiet).Background);
        }
        finally
        {
            SendKey(0x10, true);
            SendKey(0x20, true);
            SendMouse(0, 0, 4);
            window.Close();
            SetCursorPos(previousCursor.X, previousCursor.Y);
            SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static void SendKey(ushort key, bool up)
    {
        var input = new NativeInput { Type = 1, Keyboard = new() { VirtualKey = key, Flags = up ? 2u : 0u } };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<NativeInput>()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboard
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint Extra;
    }
}
