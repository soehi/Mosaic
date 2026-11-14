using System.Globalization;
using System.Windows.Data;

namespace Mosaic.Shell.Wpf;

internal sealed class NavigationIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string iconKey
            ? iconKey switch
            {
                "check-square" => "✓",
                _ => "•"
            }
            : "•";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
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
