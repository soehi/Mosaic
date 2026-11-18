using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Mosaic.Presentation.Wpf;

// The WPF focus adorner can stay attached when a pointer clicks the already-focused button.
internal sealed class ButtonFocusMark : Border
{
    private UIElement? _button;

    public ButtonFocusMark()
    {
        Loaded += Attach;
        Unloaded += Detach;
    }

    private void Attach(object sender, RoutedEventArgs args)
    {
        if (_button is not null) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not Adorner adorner) continue;
            _button = adorner.AdornedElement;
            _button.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointerDown), handledEventsToo: true);
            _button.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
            Opacity = 1;
            break;
        }
    }

    private void Detach(object sender, RoutedEventArgs args)
    {
        if (_button is null) return;
        _button.RemoveHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointerDown));
        _button.RemoveHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
        _button = null;
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs args) => Opacity = 0;
    private void OnKeyDown(object sender, KeyEventArgs args) => Opacity = 1;
}
