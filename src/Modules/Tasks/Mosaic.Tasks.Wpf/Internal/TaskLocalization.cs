using System.Windows;

namespace Mosaic.Tasks.Wpf.Internal;

internal static class TaskLocalization
{
    public static ResourceDictionary Dictionary(bool english)
    {
        var uri = new Uri($"/Mosaic.Tasks.Wpf;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative);
        return (ResourceDictionary)Application.LoadComponent(uri);
    }

    public static string Localize(string key, string fallback) =>
        Mosaic.Presentation.Wpf.MosaicLocalizationManager.TryFindResource(key) ?? fallback;

    public static string Format(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Localize(key, fallback), arguments);
}
