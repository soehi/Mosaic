using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Mosaic.DesktopPresence.Wpf;

internal partial class PresencePopupView : UserControl
{
    private readonly Func<Task> _restore;
    private readonly Func<Task> _exit;
    private readonly Func<Task> _dismiss;
    private bool _toolTipOpen;
    internal event EventHandler? InteractionChanged;
    internal bool IsFloatingInteractionActive => MoreMenu.IsOpen || _toolTipOpen;
    internal PresencePopupView(FrameworkElement view, Func<Task> restore, Func<Task> exit, Func<Task> dismiss,
        bool autoHide, int padding = 16, int cornerRadius = 12, bool showHints = false, bool compact = false)
    {
        InitializeComponent();
        ContentHost.Content = view;
        _restore = restore; _exit = exit; _dismiss = dismiss;
        Frame.Padding = new Thickness(padding);
        Frame.CornerRadius = new CornerRadius(cornerRadius);
        DismissHint.SetResourceReference(TextBlock.TextProperty, autoHide ? "Presence.AutoHideHint" : "Presence.ManualHideHint");
        DismissHint.Visibility = showHints && !compact ? Visibility.Visible : Visibility.Collapsed;
        var tip = new ToolTip(); tip.SetResourceReference(ContentControl.ContentProperty, autoHide ? "Presence.AutoHideHint" : "Presence.ManualHideHint");
        tip.Opened += (_, _) => { _toolTipOpen = true; InteractionChanged?.Invoke(this, EventArgs.Empty); };
        tip.Closed += (_, _) => { _toolTipOpen = false; InteractionChanged?.Invoke(this, EventArgs.Empty); };
        HeaderFrame.ToolTip = tip;
        MoreMenu.Opened += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        MoreMenu.Closed += (_, _) => { MoreButton.Focus(); InteractionChanged?.Invoke(this, EventArgs.Empty); };
        MoreMenu.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; MoreMenu.IsOpen = false; MoreButton.Focus(); } };
    }
    internal void CloseFloatingContent() { MoreMenu.IsOpen = false; if (HeaderFrame.ToolTip is ToolTip tip) tip.IsOpen = false; }
    internal bool CloseMenuForEscape()
    {
        if (!MoreMenu.IsOpen) return false;
        MoreMenu.IsOpen = false; MoreButton.Focus(); return true;
    }
    private void More(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton; MoreMenu.Placement = PlacementMode.Top; MoreMenu.IsOpen = true;
    }
    private async void Restore(object sender, RoutedEventArgs e) => await _restore();
    private async void Exit(object sender, RoutedEventArgs e) => await _exit();
    private async void Dismiss(object sender, RoutedEventArgs e) => await _dismiss();
}
