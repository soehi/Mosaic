using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class DragHintAdorner : Adorner
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Border _hint;
    private readonly TextBlock _text;
    private readonly Rectangle _frame;
    private readonly FrameworkElement _target;
    private Point _offset;

    public DragHintAdorner(UIElement adornedElement, string text, FrameworkElement target) : base(adornedElement)
    {
        IsHitTestVisible = false;
        _target = target;
        _text = new TextBlock { Text = text, FontSize = 12 };
        _hint = new Border
        {
            Child = _text,
            Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        _frame = new Rectangle
        {
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2.5 },
            RadiusX = 6,
            RadiusY = 6,
            IsHitTestVisible = false,
        };
        _hint.SetResourceReference(Border.BackgroundProperty, MosaicTheme.SurfaceBrushKey);
        _hint.SetResourceReference(Border.BorderBrushProperty, MosaicTheme.BorderBrushKey);
        _text.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        _frame.SetResourceReference(Shape.StrokeProperty, MosaicTheme.AccentBrushKey);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_hint);
        AddVisualChild(_canvas);
    }

    public void Update(Point position)
    {
        _offset = position;
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _canvas;

    protected override Size MeasureOverride(Size constraint)
    {
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var target = Rect.Empty;
        try { target = _target.TransformToAncestor(AdornedElement).TransformBounds(new Rect(_target.RenderSize)); }
        catch (InvalidOperationException) { /* Target left the visual tree while the drag was finishing. */ }
        _frame.Visibility = target.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!target.IsEmpty)
        {
            _frame.Width = target.Width;
            _frame.Height = target.Height;
            Canvas.SetLeft(_frame, target.X);
            Canvas.SetTop(_frame, target.Y);
        }
        var size = _hint.DesiredSize;
        var x = Math.Max(0, Math.Min(_offset.X + 14, finalSize.Width - size.Width));
        var y = _offset.Y + 14;
        if (y + size.Height > finalSize.Height) y = Math.Max(0, _offset.Y - size.Height - 10);
        Canvas.SetLeft(_hint, x);
        Canvas.SetTop(_hint, y);
        _canvas.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
