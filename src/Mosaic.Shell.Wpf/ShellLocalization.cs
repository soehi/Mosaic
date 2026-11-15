using System.Windows;

namespace Mosaic.Shell.Wpf;

internal static class ShellLocalization
{
    public static ResourceDictionary Dictionary(bool english)
    {
        var uri = new Uri($"/Mosaic.Shell.Wpf;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative);
        return (ResourceDictionary)Application.LoadComponent(uri);
    }

    public static string Localize(string key, string fallback) =>
        Mosaic.Presentation.Wpf.MosaicLocalizationManager.TryFindResource(key) ?? fallback;
}
