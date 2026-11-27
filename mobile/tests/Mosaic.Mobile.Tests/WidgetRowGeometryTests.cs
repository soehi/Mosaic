using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.Tests;

public sealed class WidgetRowGeometryTests
{
    [Theory]
    [InlineData(40, 1, 80)]
    [InlineData(50, 1, 88)]
    [InlineData(63, 1, 110)]
    [InlineData(79, 1, 120)]
    [InlineData(99, 1, 120)]
    [InlineData(100, 2, 92)]
    [InlineData(127, 2, 119)]
    [InlineData(140, 2, 120)]
    [InlineData(209, 2, 120)]
    public void Host_dimensions_are_dp_and_viewport_is_a_whole_number_of_rows(
        int heightDp, int rows, int rowHeightPx)
    {
        const float density = 2;
        var layout = WidgetRowGeometry.ForHostSize(heightDp, density);

        Assert.Equal(new WidgetRowLayout(rows, rowHeightPx), layout);
        Assert.True(layout.Rows * layout.RowHeightPx <= heightDp * density);
    }

    [Fact]
    public void Fractional_pixels_are_rounded_down_before_sizing_the_viewport()
    {
        var layout = WidgetRowGeometry.ForHostSize(101.5f, 1.3f);

        Assert.Equal(new WidgetRowLayout(2, 60), layout);
    }

    [Fact]
    public void Missing_or_invalid_host_metrics_fall_back_to_one_row_at_mdpi()
    {
        var layout = WidgetRowGeometry.ForHostSize(float.NaN, 0);

        Assert.Equal(new WidgetRowLayout(1, 44), layout);
    }
}
