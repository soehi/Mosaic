using System.Windows;

namespace Mosaic.Wpf.Tests;

// AccountView resolves theme brushes with StaticResource while its BAML is parsed, so a test that
// constructs the view needs Application.Resources to hold Theme.xaml first. Only one Application
// can exist per process, so every test that parses such a view goes through this helper instead of
// relying on another test having created one.
internal static class UiTestApplication
{
    private static readonly object Gate = new();

    internal static void EnsureTheme()
    {
        lock (Gate)
        {
            if (Application.Current is not null) return;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative)
            });
        }
    }
}