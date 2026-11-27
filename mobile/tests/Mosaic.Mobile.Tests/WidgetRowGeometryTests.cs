using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.Tests;

/// <summary>
/// Pins the geometry rule: the card shows whole rows only. Both counts and the
/// per-row pixel height are asserted against the actual density 320 measurement
/// that produced the clipped fourth row in 0.3.3.
/// </summary>
public sealed class WidgetRowGeometryTests
{
    private const int Density320 = 320;

    [Theory]
    [InlineData(40, 1)]
    [InlineData(50, 1)]
    [InlineData(79, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(127, 2)]
    [InlineData(140, 2)]
    [InlineData(180, 2)]
    public void Row_count_is_one_below_two_rows_and_two_at_or_above(int heightDp, int expected)
    {
        var heightPx = heightDp * Density320 / 160;

        var layout = WidgetRowGeometry.Compute(heightPx, Density320, dividerPx: 2);

        Assert.Equal(expected, layout.Rows);
    }

    [Fact]
    public void A_one_row_card_never_leaves_a_second_row_partially_visible()
    {
        // 4x1 is measured at about 70dp on this host; its usable content height
        // after the 8dp padding is 70dp minus 16dp.
        var availablePx = 54 * Density320 / 160;

        var layout = WidgetRowGeometry.Compute(availablePx, Density320, dividerPx: 2);

        Assert.Equal(1, layout.Rows);
        Assert.Equal(availablePx, layout.RowHeightPx);
    }

    [Fact]
    public void Two_rows_share_the_usable_height_exactly()
    {
        var availablePx = 222; // 111dp of content at density 320

        var layout = WidgetRowGeometry.Compute(availablePx, Density320, dividerPx: 2);

        Assert.Equal(2, layout.Rows);
        Assert.Equal((availablePx - 2) / 2, layout.RowHeightPx);
        Assert.True(2 * layout.RowHeightPx + 2 <= availablePx);
    }

    [Fact]
    public void Row_count_is_never_above_the_declared_maximum()
    {
        var layout = WidgetRowGeometry.Compute(1000, Density320, dividerPx: 2);

        Assert.Equal(WidgetRowGeometry.MaxRows, layout.Rows);
    }

    [Fact]
    public void A_short_card_still_fits_a_single_row()
    {
        var layout = WidgetRowGeometry.Compute(0, Density320, dividerPx: 2);

        Assert.Equal(1, layout.Rows);
        Assert.True(layout.RowHeightPx >= 1);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(-1, 40)]
    [InlineData(40, 40)]
    [InlineData(140, 70)]
    [InlineData(500, 140)]
    public void Reported_height_is_clamped_into_the_declared_range(int reported, int expectedDp)
    {
        var actual = WidgetRowGeometry.ReportedHeightToPixels(reported, Density320);

        Assert.Equal(expectedDp, actual * 160 / Density320);
    }

    [Fact]
    public void The_measured_host_value_produces_two_full_rows()
    {
        // The host measured minH=209px at density 320 for a two-cell card.
        // Reported values are already pixels, not dp.
        var heightPx = WidgetRowGeometry.ReportedHeightToPixels(209, Density320);

        var layout = WidgetRowGeometry.Compute(heightPx, Density320, dividerPx: 2);

        Assert.Equal(2, layout.Rows);
        Assert.Equal(103, layout.RowHeightPx);
    }

    [Fact]
    public void Density_fallback_keeps_the_geometry_finite()
    {
        var layout = WidgetRowGeometry.Compute(140, densityDpi: 0, dividerPx: 0);

        Assert.Equal(2, layout.Rows);
        Assert.Equal(70, layout.RowHeightPx);
    }
}
