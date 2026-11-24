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

/// <summary>
/// Shows the value when it carries text, otherwise the localized fallback declared by the parameter
/// as <c>ResourceKey|默认文案</c> so a missing device or platform label follows the current language.
/// </summary>
internal sealed class LocalizedFallbackConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? text : LocalizedLabelConverter.Resolve(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Renders the localized text named by the parameter (<c>ResourceKey|默认文案</c>), ignoring the value.</summary>
internal sealed class LocalizedLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Resolve(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    internal static string Resolve(object? parameter)
    {
        var spec = parameter as string;
        if (string.IsNullOrEmpty(spec)) return "";
        var separator = spec.IndexOf('|', StringComparison.Ordinal);
        return separator < 0
            ? AuthLocalization.Localize(spec, spec)
            : AuthLocalization.Localize(spec[..separator], spec[(separator + 1)..]);
    }
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
