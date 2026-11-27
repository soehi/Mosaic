using Android.Appwidget;
using Android.Content;
using Android.Content.Res;
using Android.Util;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

internal readonly record struct WidgetViewport(int Rows, int RowHeightPx, bool ShowMark)
{
    public const string RowHeightExtra = "mosaic.widget.rowHeight";
    public const string MarkExtra = "mosaic.widget.mark";

    public static RemoteViews ForHost(Context context, AppWidgetManager manager, int id, Func<WidgetViewport, RemoteViews> build)
    {
        var options = manager.GetAppWidgetOptions(id);
        var metrics = context.Resources!.DisplayMetrics!;
        var fontScale = context.Resources.Configuration?.FontScale ?? 1;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
#pragma warning disable CA1422
            var sizes = options?.GetParcelableArrayList(AppWidgetManager.OptionAppwidgetSizes);
#pragma warning restore CA1422
            if (sizes is { Count: > 0 })
            {
                var views = new Dictionary<SizeF, RemoteViews>();
                foreach (var size in sizes.OfType<SizeF>().Take(16))
                    views.TryAdd(size, build(Measure(size.Width, size.Height, metrics.Density, fontScale)));
                if (views.Count > 0) return new RemoteViews(views);
            }
        }

        var portrait = Measure(options?.GetInt(AppWidgetManager.OptionAppwidgetMinWidth, 0) ?? 0,
            options?.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight, 0) ?? 0, metrics.Density, fontScale);
        var landscape = Measure(options?.GetInt(AppWidgetManager.OptionAppwidgetMaxWidth, 0) ?? 0,
            options?.GetInt(AppWidgetManager.OptionAppwidgetMinHeight, 0) ?? 0, metrics.Density, fontScale);
        return new RemoteViews(build(landscape), build(portrait));
    }

    private static WidgetViewport Measure(float width, float height, float density, float fontScale)
    {
        var layout = WidgetRowGeometry.ForHostSize(height, density);
        return new WidgetViewport(layout.Rows, layout.RowHeightPx, layout.Rows == 2 && width >= 220 && fontScale <= 1.15f);
    }
}
