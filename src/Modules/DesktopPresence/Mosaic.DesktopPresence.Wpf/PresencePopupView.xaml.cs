using System.Windows;
using System.Windows.Controls;

namespace Mosaic.DesktopPresence.Wpf;

internal partial class PresencePopupView : UserControl
{
    private readonly Func<Task> _restore;
    private readonly Func<Task> _exit;
    private readonly Func<Task> _dismiss;
    internal PresencePopupView(FrameworkElement view, Func<Task> restore, Func<Task> exit, Func<Task> dismiss, bool autoHide)
    {
        InitializeComponent();
        ContentHost.Content = view;
        _restore = restore; _exit = exit; _dismiss = dismiss;
        DismissHint.SetResourceReference(TextBlock.TextProperty, autoHide ? "Presence.AutoHideHint" : "Presence.ManualHideHint");
        SizeChanged += (_, _) =>
        {
            var compact = ActualHeight < 360;
            BrandLabel.Visibility = Subtitle.Visibility = DismissHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            HeaderFrame.Padding = compact ? new Thickness(14, 10, 12, 10) : new Thickness(18, 16, 14, 14);
        };
    }
    private async void Restore(object sender, RoutedEventArgs e) => await _restore();
    private async void Exit(object sender, RoutedEventArgs e) => await _exit();
    private async void Dismiss(object sender, RoutedEventArgs e) => await _dismiss();
}
