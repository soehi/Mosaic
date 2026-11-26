using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;

namespace Mosaic.Mobile.App.Platforms.Android.Widgets;

/// <summary>
/// The AppWidget provider. Android may deliver a broadcast before the MAUI
/// application has finished creating its container, so every callback defers to
/// <see cref="WidgetBroadcastScope"/> and simply skips when DI is not ready — the
/// system re-delivers APPWIDGET_UPDATE later. The widget never depends on
/// MainActivity, AppShell or StartupService having run.
/// </summary>
[BroadcastReceiver(
    Name = "app.mosaic.tasks.TaskWidgetProvider",
    Exported = false,
    Label = "@string/widget_label")]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_tasks_info")]
public sealed class TaskWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null || appWidgetIds.Length == 0)
        {
            return;
        }

        var scope = GoAsync()!;
        _ = Task.Run(async () =>
        {
            try
            {
                if (WidgetBroadcastScope.TryResolve<WidgetRefresh>(context, out var refresh))
                {
                    await refresh.RefreshAllAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A failed refresh leaves the previous rendering; the system will
                // deliver another APPWIDGET_UPDATE on the next trigger.
            }
            finally
            {
                scope.Finish();
            }
        });
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

        var scope = GoAsync()!;
        _ = Task.Run(async () =>
        {
            try
            {
                if (WidgetBroadcastScope.TryResolve<WidgetRefresh>(context, out var refresh))
                {
                    await refresh.RefreshAsync(appWidgetId).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best effort; the instance keeps its previous layout.
            }
            finally
            {
                scope.Finish();
            }
        });
    }
}
