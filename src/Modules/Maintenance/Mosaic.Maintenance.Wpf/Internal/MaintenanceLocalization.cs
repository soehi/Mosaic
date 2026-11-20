using Mosaic.Presentation.Wpf;

namespace Mosaic.Maintenance.Wpf.Internal;

internal static class MaintenanceLocalization
{
    public static string Localize(string key, string fallback) =>
        MosaicLocalizationManager.TryFindResource(key) ?? fallback;
}

internal static class MaintenanceLocalizationDictionaries
{
    public static System.Windows.ResourceDictionary Dictionary(bool english)
    {
        var uri = new Uri($"/Mosaic.Maintenance.Wpf;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative);
        return (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(uri);
    }
}
