using Mosaic.Presentation.Wpf;

namespace Mosaic.Desktop;

internal static class DesktopLocalization
{
    internal static IDisposable Register() =>
        MosaicLocalizationManager.Register(
            "desktop",
            language => Dictionary(language == MosaicLanguage.English));

    internal static string Localize(string key, string fallback) =>
        MosaicLocalizationManager.TryFindResource(key) ?? fallback;

    private static System.Windows.ResourceDictionary Dictionary(bool english) =>
        (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri($"/Mosaic.Desktop;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative));
}
