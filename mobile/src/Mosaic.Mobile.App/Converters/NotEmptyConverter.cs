using System.Globalization;

namespace Mosaic.Mobile.App.Converters;

/// <summary>Shows a label only when its bound text is non-empty.</summary>
public sealed class NotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            string text => !string.IsNullOrWhiteSpace(text),
            bool flag => flag,
            null => false,
            _ => true,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
