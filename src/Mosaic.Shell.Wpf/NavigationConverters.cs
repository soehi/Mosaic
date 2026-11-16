using System.Globalization;
using System.Windows.Data;

namespace Mosaic.Shell.Wpf;

internal sealed class NavigationIconConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var iconKey = values.ElementAtOrDefault(0) as string;
        var label = values.ElementAtOrDefault(1) as string;
        return iconKey switch
        {
            "check-square" => "✓",
            "folder" => "▰",
            "calendar" => "◷",
            "settings" => string.Empty,
            _ => string.IsNullOrWhiteSpace(label) ? "•" : StringInfo.GetNextTextElement(label)
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
