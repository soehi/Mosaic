using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Mosaic.Shell.Wpf;

internal sealed class NavigationIconConverter : IMultiValueConverter
{
    private static readonly Geometry Tasks = CreateGeometry(
        "F0 M4 3 L20 3 20 21 4 21 Z M6 5 L6 19 18 19 18 5 Z " +
        "M7 11 L9 9 11 11 15 7 17 9 11 15 Z");
    private static readonly Geometry Folder = CreateGeometry(
        "F0 M2 5 L10 5 12 7 22 7 22 20 2 20 Z M4 9 L4 18 20 18 20 9 Z");
    private static readonly Geometry Calendar = CreateGeometry(
        "F0 M6 2 L8 2 8 4 16 4 16 2 18 2 18 4 21 4 21 22 3 22 3 4 6 4 Z " +
        "M5 10 L5 20 19 20 19 10 Z M5 6 L5 8 19 8 19 6 Z");
    private static readonly Geometry Settings = CreateGeometry(
        "F0 M9 2 L15 2 15.7 5.1 17 5.9 20.1 5 23.1 10.2 20.8 12.4 " +
        "20.8 13.9 23.1 16.1 20.1 21.3 17 20.4 15.7 21.2 15 24.3 " +
        "9 24.3 8.3 21.2 7 20.4 3.9 21.3 0.9 16.1 3.2 13.9 3.2 12.4 " +
        "0.9 10.2 3.9 5 7 5.9 8.3 5.1 Z " +
        "M16 13.15 A4 4 0 1 0 8 13.15 A4 4 0 1 0 16 13.15 Z");
    private static readonly Geometry Default = CreateGeometry(
        "F0 M4 3 L20 3 20 21 4 21 Z M6 5 L6 19 18 19 18 5 Z");

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var iconKey = values.ElementAtOrDefault(0) as string;
        return iconKey switch
        {
            "check-square" => Tasks,
            "folder" => Folder,
            "calendar" => Calendar,
            "settings" => Settings,
            _ => Default
        };
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Geometry CreateGeometry(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
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
