using System.Windows;

namespace Mosaic.Presentation.Wpf;

public static class MosaicTheme
{
    public static ComponentResourceKey CanvasBrushKey { get; } = new(typeof(MosaicTheme), nameof(CanvasBrushKey));
    public static ComponentResourceKey SurfaceBrushKey { get; } = new(typeof(MosaicTheme), nameof(SurfaceBrushKey));
    public static ComponentResourceKey SurfaceMutedBrushKey { get; } = new(typeof(MosaicTheme), nameof(SurfaceMutedBrushKey));
    public static ComponentResourceKey SidebarBrushKey { get; } = new(typeof(MosaicTheme), nameof(SidebarBrushKey));
    public static ComponentResourceKey BorderBrushKey { get; } = new(typeof(MosaicTheme), nameof(BorderBrushKey));
    public static ComponentResourceKey TextPrimaryBrushKey { get; } = new(typeof(MosaicTheme), nameof(TextPrimaryBrushKey));
    public static ComponentResourceKey TextSecondaryBrushKey { get; } = new(typeof(MosaicTheme), nameof(TextSecondaryBrushKey));
    public static ComponentResourceKey TextMutedBrushKey { get; } = new(typeof(MosaicTheme), nameof(TextMutedBrushKey));
    public static ComponentResourceKey AccentBrushKey { get; } = new(typeof(MosaicTheme), nameof(AccentBrushKey));
    public static ComponentResourceKey AccentTextBrushKey { get; } = new(typeof(MosaicTheme), nameof(AccentTextBrushKey));
    public static ComponentResourceKey AccentButtonBackgroundBrushKey { get; } = new(typeof(MosaicTheme), nameof(AccentButtonBackgroundBrushKey));
    public static ComponentResourceKey AccentButtonTextBrushKey { get; } = new(typeof(MosaicTheme), nameof(AccentButtonTextBrushKey));
    public static ComponentResourceKey AccentHoverBrushKey { get; } = new(typeof(MosaicTheme), nameof(AccentHoverBrushKey));
    public static ComponentResourceKey AccentSoftBrushKey { get; } = new(typeof(MosaicTheme), nameof(AccentSoftBrushKey));
    public static ComponentResourceKey DangerBrushKey { get; } = new(typeof(MosaicTheme), nameof(DangerBrushKey));
    public static ComponentResourceKey DangerSoftBrushKey { get; } = new(typeof(MosaicTheme), nameof(DangerSoftBrushKey));
    public static ComponentResourceKey DangerBorderBrushKey { get; } = new(typeof(MosaicTheme), nameof(DangerBorderBrushKey));
    public static ComponentResourceKey FocusBrushKey { get; } = new(typeof(MosaicTheme), nameof(FocusBrushKey));
    public static ComponentResourceKey ControlHoverBorderBrushKey { get; } = new(typeof(MosaicTheme), nameof(ControlHoverBorderBrushKey));
    public static ComponentResourceKey SelectionBrushKey { get; } = new(typeof(MosaicTheme), nameof(SelectionBrushKey));
    public static ComponentResourceKey ScrollThumbBrushKey { get; } = new(typeof(MosaicTheme), nameof(ScrollThumbBrushKey));
    public static ComponentResourceKey ScrollThumbHoverBrushKey { get; } = new(typeof(MosaicTheme), nameof(ScrollThumbHoverBrushKey));
    public static ComponentResourceKey DisabledSurfaceBrushKey { get; } = new(typeof(MosaicTheme), nameof(DisabledSurfaceBrushKey));
    public static ComponentResourceKey DisabledTextBrushKey { get; } = new(typeof(MosaicTheme), nameof(DisabledTextBrushKey));
    public static ComponentResourceKey AccentButtonKey { get; } = new(typeof(MosaicTheme), nameof(AccentButtonKey));
    public static ComponentResourceKey DangerButtonKey { get; } = new(typeof(MosaicTheme), nameof(DangerButtonKey));
    public static ComponentResourceKey QuietButtonKey { get; } = new(typeof(MosaicTheme), nameof(QuietButtonKey));
    public static ComponentResourceKey HeaderTextKey { get; } = new(typeof(MosaicTheme), nameof(HeaderTextKey));
    public static ComponentResourceKey BodyTextKey { get; } = new(typeof(MosaicTheme), nameof(BodyTextKey));
    public static ComponentResourceKey CaptionTextKey { get; } = new(typeof(MosaicTheme), nameof(CaptionTextKey));
}
