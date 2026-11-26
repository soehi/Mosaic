using Android.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;

namespace Mosaic.Mobile.App.Platforms.Android.Widgets;

/// <summary>
/// Resolves the long-running MAUI container from a broadcast. The application is
/// created before any receiver runs, so the widget path reuses its DI container,
/// its token store and its projection — it never builds a parallel stack.
/// </summary>
internal static class WidgetBroadcastScope
{
    internal static bool TryResolve<T>(Context context, out T service)
        where T : notnull
    {
        service = default!;
        try
        {
            if (context.ApplicationContext is MauiApplication
                && IPlatformApplication.Current?.Services is { } services)
            {
                service = services.GetRequiredService<T>();
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }
}
