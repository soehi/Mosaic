using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Tasks.Wpf.Internal;

/// <summary>
/// Drag feedback for the peek list: a dashed frame over the dragged row plus a hint bubble.
/// The bubble is positioned through a render transform so a pointer move costs one compositing
/// update instead of a measure/arrange pass over the whole adorner layer.
/// </summary>
internal sealed class DragHintAdorner : Adorner
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Border _hint;
    private readonly TextBlock _text;
    private readonly Rectangle _frame;
    private readonly FrameworkElement _target;
    private readonly TranslateTransform _hintOffset = new();
    private Size _hintSize;
    private Point _offset;
    private bool _measured;

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
            RenderTransform = _hintOffset,
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
        // 逐帧只写合成变换：不失效测量与重排，拖拽期间 AdornerLayer 不再整层重新布局。
        Place(position);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _canvas;

    protected override Size MeasureOverride(Size constraint)
    {
        // The canvas is arranged at the adorned element's size; nothing here depends on the
        // bubble's size until the first arrange, so the pass stays free of work per frame.
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_measured)
        {
            _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _hintSize = _hint.DesiredSize;
            _measured = true;
        }

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

        _canvas.Arrange(new Rect(finalSize));
        Place(_offset);
        return finalSize;
    }

    /// <summary>Moves the bubble without a layout pass; clamped to the adorner's own bounds.</summary>
    private void Place(Point position)
    {
        var area = AdornedElement.RenderSize;
        var x = Math.Max(0, Math.Min(position.X + 14, area.Width - _hintSize.Width));
        var y = position.Y + 14;
        if (y + _hintSize.Height > area.Height) y = Math.Max(0, position.Y - _hintSize.Height - 10);
        _hintOffset.X = x;
        _hintOffset.Y = y;
    }
}
