using System.Globalization;
using System.Windows.Data;
using Mosaic.Contracts;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Auth.Wpf.Internal;

/// <summary>Formats a UTC timestamp in the user's local timezone for the session list.</summary>
internal sealed class LocalDateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset instant && instant > DateTimeOffset.MinValue
            ? instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Renders the localized "this device" tag for the current session, empty otherwise.</summary>
internal sealed class CurrentSessionTagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? AuthLocalization.Localize("Auth.Sessions.CurrentTag", "（本设备）")
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Inverts a boolean, used to hide the compact page while the welcome layout shows.</summary>
internal sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Inverted boolean→visibility so the compact page collapses while the welcome layout shows.</summary>
internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Accent border for the theme-switch button matching the active variant, transparent otherwise.</summary>
internal sealed class ActiveThemeBorderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = value is MosaicThemeVariant variant && parameter is MosaicThemeVariant wanted && variant == wanted;
        if (!active) return System.Windows.Media.Brushes.Transparent;
        var res = System.Windows.Application.Current?.TryFindResource(MosaicTheme.AccentBrushKey);
        return res as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
