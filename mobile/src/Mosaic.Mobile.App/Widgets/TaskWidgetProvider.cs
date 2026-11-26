using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Desktop-widget provider. Updates run through <see cref="BroadcastReceiver.GoAsync"/>
/// with a hard budget so the process never holds a broadcast open past the
/// platform limit; no Activity is created at any point.
/// </summary>
[BroadcastReceiver(Label = "@string/widget_label", Exported = false)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_tasks_info")]
public sealed class TaskWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
        {
            return;
        }

        RunRefresh(context);
    }

    public override void OnAppWidgetOptionsChanged(
        Context? context,
        AppWidgetManager? appWidgetManager,
        int appWidgetId,
        Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null)
        {
            return;
        }

        var pending = GoAsync();
        var appContext = context;
        var last = WidgetRuntime.LastModel;
        _ = Task.Run(async () =>
        {
            try
            {
                var model = last ?? await WidgetRuntime.RefreshAsync().ConfigureAwait(false);
                WidgetRuntime.RenderAll(appContext, model);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }

    private void RunRefresh(Context context)
    {
        var pending = GoAsync();
        var appContext = context;
        _ = Task.Run(async () =>
        {
            try
            {
                var model = await WidgetRuntime.RefreshAsync().ConfigureAwait(false);
                WidgetRuntime.RenderAll(appContext, model);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}