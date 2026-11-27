namespace Mosaic.Mobile.Core.Cards;

/// <summary>How many rows fit in the viewport and how tall each row is in pixels.</summary>
public readonly record struct WidgetRowLayout(int Rows, int RowHeightPx);

public static class WidgetRowGeometry
{
    private const int TwoRowThresholdDp = 100;

    /// <summary>Sizes a scrollable viewport using the host's documented dp dimensions.</summary>
    public static WidgetRowLayout ForHostSize(float heightDp, float density)
    {
        var scale = float.IsFinite(density) && density > 0 ? density : 1;
        var height = float.IsFinite(heightDp) && heightDp > 0 ? heightDp : 44;
        var rows = height >= TwoRowThresholdDp ? 2 : 1;
        var rowHeightDp = Math.Clamp((height - 8) / rows, Math.Min(44, height / rows), 60);
        return new WidgetRowLayout(rows, Math.Max(1, (int)Math.Floor(rowHeightDp * scale)));
    }
}
