using System.Globalization;
using System.Windows.Data;

namespace Mosaic.Shell.Wpf;

// Navigation glyphs all come from Segoe MDL2 Assets so that every entry shares one visual
// language, one baseline and one selected-state colour (the TextBlock inherits Button.Foreground).
internal sealed class NavigationIconConverter : IMultiValueConverter
{
    internal const string DefaultGlyph = "\uE8A5"; // Document

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var iconKey = values.ElementAtOrDefault(0) as string;
        return iconKey switch
        {
            "check-square" => "\uE9D5", // CheckList
            "folder" => "\uE8B7", // Folder
            "calendar" => "\uE787", // Calendar
            "settings" => "\uE713", // Settings
            "account" => "\uE77B", // Contact
            _ => DefaultGlyph
        };
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class NavigationItemEqualsConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 &&
        values[0] is string itemId &&
        values[1] is string currentId &&
        string.Equals(itemId, currentId, StringComparison.Ordinal);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}