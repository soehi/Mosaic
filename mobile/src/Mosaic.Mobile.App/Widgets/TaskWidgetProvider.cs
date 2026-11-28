using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Desktop-widget provider. Updates run through <see cref="BroadcastReceiver.GoAsync"/>
/// with a hard budget so the process never holds a broadcast open past the
/// platform limit; no Activity is created at any point.
///
/// <para>
/// This receiver must be exported. <c>ACTION_APPWIDGET_UPDATE</c> and the other
/// <c>APPWIDGET_*</c> broadcasts are sent by the system AppWidgetService, which
/// is a different UID; an <c>exported="false"</c> receiver never receives them,
/// so the widget would stay on its <c>initialLayout</c> forever. Because it is
/// exported, <see cref="OnReceive"/> accepts only the system widget actions and
/// ignores everything else.
/// </para>
/// </summary>
[BroadcastReceiver(Label = "@string/widget_label", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_tasks_info")]
public sealed class TaskWidgetProvider : AppWidgetProvider
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        if (!IsSystemWidgetAction(intent.Action)) return;

        base.OnReceive(context, intent);
    }

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
        {
            return;
        }

        RunRefresh(context);
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        if (context is null || appWidgetIds is null)
        {
            return;
        }

        // The host reuses nothing, but a stale group key would outlive its
        // widget and could be read by a future instance with the same id.
        foreach (var appWidgetId in appWidgetIds)
        {
            WidgetGroupStore.Delete(context, appWidgetId);
            WidgetRenderer.ClearRefreshing(appWidgetId);
        }

        base.OnDeleted(context, appWidgetIds);
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
                var model = last ?? await WidgetRuntime.RefreshAsync(appContext).ConfigureAwait(false);
                WidgetRuntime.RenderAll(appContext, model);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }

    private static bool IsSystemWidgetAction(string? action) =>
        action is AppWidgetManager.ActionAppwidgetUpdate
            or AppWidgetManager.ActionAppwidgetDeleted
            or AppWidgetManager.ActionAppwidgetOptionsChanged
            or AppWidgetManager.ActionAppwidgetEnabled
            or AppWidgetManager.ActionAppwidgetDisabled
            or AppWidgetManager.ActionAppwidgetRestored;

    private void RunRefresh(Context context)
    {
        var pending = GoAsync();
        var appContext = context;
        _ = Task.Run(async () =>
        {
            try
            {
                var model = await WidgetRuntime.RefreshAsync(appContext).ConfigureAwait(false);
                WidgetRuntime.RenderAll(appContext, model);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}