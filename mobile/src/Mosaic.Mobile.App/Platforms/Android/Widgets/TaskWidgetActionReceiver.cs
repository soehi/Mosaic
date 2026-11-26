using Android.App;
using Android.Appwidget;
using Android.Content;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.App.Platforms.Android.Widgets;

/// <summary>
/// Handles the widget's own button broadcasts. The PendingIntent carries only
/// taskId, action and appWidgetId — no token, no email, no payload. The task is
/// re-read from the current account's projection before any write, so an intent
/// captured under a previous session or account cannot act: the lookup refuses
/// it, and the widget shows an explicit failure instead of a local fake write.
/// </summary>
[BroadcastReceiver(
    Name = "app.mosaic.tasks.TaskWidgetActionReceiver",
    Exported = false,
    Label = "@string/widget_label")]
[IntentFilter(new[] { WidgetFormatter.ActionRefresh, WidgetFormatter.ActionTask })]
public sealed class TaskWidgetActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action is null)
        {
            return;
        }

        var scope = GoAsync()!;
        var action = intent.Action;
        var taskIdText = intent.GetStringExtra(WidgetFormatter.ExtraTaskId);
        var taskActionValue = intent.GetIntExtra(WidgetFormatter.ExtraAction, -1);
        var appWidgetId = intent.GetIntExtra(
            WidgetFormatter.ExtraWidgetId,
            AppWidgetManagerCompat.InvalidAppwidgetId);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(context, action, taskIdText, taskActionValue, appWidgetId)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed action must never crash the process; the widget keeps
                // its previous state and the user can retry or refresh.
            }
            finally
            {
                scope.Finish();
            }
        });
    }

    private static async Task RunAsync(
        Context context,
        string action,
        string? taskIdText,
        int taskActionValue,
        int appWidgetId)
    {
        if (!WidgetBroadcastScope.TryResolve<WidgetRefresh>(context, out var refresh))
        {
            return;
        }

        if (action == WidgetFormatter.ActionRefresh)
        {
            await refresh.RefreshAllAsync().ConfigureAwait(false);
            return;
        }

        if (appWidgetId == AppWidgetManagerCompat.InvalidAppwidgetId)
        {
            return;
        }

        if (!Guid.TryParse(taskIdText, out var taskId)
            || taskActionValue < 0
            || !Enum.IsDefined(typeof(WidgetTaskAction), (WidgetTaskAction)taskActionValue))
        {
            await refresh.RefreshAsync(appWidgetId).ConfigureAwait(false);
            return;
        }

        var taskAction = (WidgetTaskAction)taskActionValue;
        if (!WidgetBroadcastScope.TryResolve<WidgetTaskRepository>(context, out var repository))
        {
            return;
        }

        var contextInfo = await repository.EstablishAsync().ConfigureAwait(false);
        if (contextInfo is null)
        {
            await refresh.RefreshAsync(
                appWidgetId,
                repository.Text(StringKeys.WidgetSessionExpiredWrite)).ConfigureAwait(false);
            return;
        }

        var task = await repository.FindTaskAsync(taskId, contextInfo).ConfigureAwait(false);
        if (task is null)
        {
            // Unknown under the current account: the intent predates a sign-out,
            // account switch or sync. Show it, never write locally.
            await refresh.RefreshAsync(
                appWidgetId,
                repository.Text(StringKeys.WidgetUnknownTask)).ConfigureAwait(false);
            return;
        }

        var result = await repository.WriteAsync(task, taskAction, contextInfo).ConfigureAwait(false);
        if (result.Status == WidgetWriteStatus.SessionExpired)
        {
            await repository.ExpireSessionAsync().ConfigureAwait(false);
        }

        await refresh.RefreshAsync(appWidgetId, repository.Describe(result)).ConfigureAwait(false);
        // A successful write changes server state; sync other surfaces too.
        if (result.Status is WidgetWriteStatus.Applied or WidgetWriteStatus.Replayed)
        {
            await refresh.RefreshAllAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Shields the receiver from the AppWidgetManager binding at compile time.</summary>
internal static class AppWidgetManagerCompat
{
    internal const int InvalidAppwidgetId = global::Android.Appwidget.AppWidgetManager.InvalidAppwidgetId;
}
