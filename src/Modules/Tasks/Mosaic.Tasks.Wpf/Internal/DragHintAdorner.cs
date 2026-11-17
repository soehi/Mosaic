using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class DragHintAdorner : Adorner
{
    private readonly TextBlock _text;
    private Point _offset;

    public DragHintAdorner(UIElement adornedElement, string text) : base(adornedElement)
    {
        _text = new TextBlock
        {
            Text = text,
            Padding = new Thickness(8),
            Background = SystemColors.InfoBrush,
            Foreground = SystemColors.InfoTextBrush,
        };
    }

    public void Update(Point position) => _offset = position;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _text;

    protected override Size MeasureOverride(Size constraint)
    {
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _text.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var position = new Point(_offset.X + 12, _offset.Y + 12);
        var width = _text.DesiredSize.Width;
        var height = _text.DesiredSize.Height;
        if (position.X + width > finalSize.Width) position.X = Math.Max(0, finalSize.Width - width);
        if (position.Y + height > finalSize.Height) position.Y = Math.Max(0, finalSize.Height - height);
        _text.Arrange(new Rect(position, new Size(width, height)));
        return finalSize;
    }
}
