using System.Globalization;
using System.Windows.Data;
using Mosaic.Contracts;

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
