using Android.Appwidget;
using Android.Content;
using Android.Util;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// One tag shared by the widget's render path, factory and receiver. Logging the
/// entry points is the only way to see, from a headless adb session, whether the
/// launcher reached our code or silently kept a cached RemoteViews.
/// </summary>
internal static class WidgetDiagnostics
{
    internal const string Tag = "MosaicWidget";

    public static void LogOptions(Context context, int appWidgetId)
    {
        try
        {
            var manager = AppWidgetManager.GetInstance(context);
            var options = manager?.GetAppWidgetOptions(appWidgetId);
            if (options is null)
            {
                return;
            }

            Log.Info(Tag, string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"options id={appWidgetId} minW={options.GetInt(AppWidgetManager.OptionAppwidgetMinWidth, -1)} maxW={options.GetInt(AppWidgetManager.OptionAppwidgetMaxWidth, -1)} minH={options.GetInt(AppWidgetManager.OptionAppwidgetMinHeight, -1)} maxH={options.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight, -1)}"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warn(Tag, "options unavailable: " + exception.Message);
        }
    }
}
