using System.Globalization;

namespace Mosaic.Mobile.Core.Cards;

/// <summary>How many rows fit and how tall each one is, in pixels.</summary>
public readonly record struct WidgetRowLayout(int Rows, int RowHeightPx);

/// <summary>
/// Row sizing for the widget list. A <see cref="Android.Widget.ListView"/> fills
/// whatever height its container gives it, so a fixed row height leaves a
/// partially visible row at the bottom. This divides the usable height into a
/// whole number of rows instead, which is what makes "4×1 shows one task, 4×2
/// shows two" true rather than approximate.
/// </summary>
public static class WidgetRowGeometry
{
    /// <summary>Below this a row's title becomes hard to read.</summary>
    public const int MinRowHeightDp = 40;

    /// <summary>
    /// The widget is declared as at most two rows tall; more rows would need a
    /// taller card than <c>maxResizeHeight</c> allows.
    /// </summary>
    public const int MaxRows = 2;

    /// <summary>
    /// Height at which the card counts as two rows. The platform's own cell
    /// arithmetic puts a one-cell card near 70dp and a two-cell card near 140dp,
    /// so 100dp splits them with margin on both sides.
    /// </summary>
    public const int TwoRowThresholdDp = 100;

    /// <summary>The declared height range from <c>widget_tasks_info.xml</c>.</summary>
    public const int MinDeclaredHeightDp = 40;

    public const int MaxDeclaredHeightDp = 140;

    /// <summary>
    /// Turns the host-reported widget height into pixels.
    /// <para>
    /// <c>OPTION_APPWIDGET_MIN_HEIGHT</c> is documented as dp, but AOSP-derived
    /// launchers (including the one measured here) report pixels: a one-cell
    /// card arrives as 134 with density 320, which is 67dp and not 134dp. The
    /// value is therefore read as pixels and clamped into the declared range,
    /// so a launcher that really does report dp degrades to one row fewer rather
    /// than to a clipped row — the failure this class exists to prevent.
    /// </para>
    /// </summary>
    public static int ReportedHeightToPixels(int reported, int densityDpi)
    {
        var density = densityDpi > 0 ? densityDpi : 160;
        if (reported <= 0)
        {
            // No usable option: assume the smallest card, one row.
            return MinDeclaredHeightDp * density / 160;
        }

        var minPx = MinDeclaredHeightDp * density / 160;
        var maxPx = MaxDeclaredHeightDp * density / 160;
        return Math.Clamp(reported, minPx, maxPx);
    }

    /// <summary>
    /// Splits the usable content height into whole rows.
    /// <paramref name="availableHeightPx"/> is what is left after the card's
    /// padding; <paramref name="dividerPx"/> is the height of one divider, of
    /// which <c>Rows - 1</c> are drawn.
    /// </summary>
    public static WidgetRowLayout Compute(int availableHeightPx, int densityDpi, int dividerPx)
    {
        var density = densityDpi > 0 ? densityDpi : 160;
        var availableDp = availableHeightPx * 160 / density;

        var rows = availableDp >= TwoRowThresholdDp ? 2 : 1;

        // A card too short for its row count drops to what physically fits.
        var minRowsDp = MinRowHeightDp * rows;
        if (availableDp < minRowsDp)
        {
            rows = Math.Max(1, availableDp / MinRowHeightDp);
        }

        rows = Math.Clamp(rows, 1, MaxRows);

        var dividers = Math.Max(0, rows - 1) * dividerPx;
        var rowHeightPx = Math.Max(1, (availableHeightPx - dividers) / rows);
        return new WidgetRowLayout(rows, rowHeightPx);
    }
}
