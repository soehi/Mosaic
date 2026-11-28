using Android.App;
using Android.Content;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// The widget's only broadcast surface. The visible UI exposes the complete
/// circle and a dedicated refresh button. Archive remains reachable by name for
/// the kernel and tests but has no card entry point. The receiver is not
/// exported, so no other app can invoke it.
///
/// A stale task PendingIntent is harmless across account switches: the command
/// looks the task id up inside the current account's projection, so an id from a
/// previous account simply returns "任务不在本地投影中". The refresh intent
/// carries no task id and is bound to this receiver's own component.
/// </summary>
[BroadcastReceiver(Exported = false)]
[IntentFilter([ActionTask, ActionRefresh])]
public sealed class WidgetActionReceiver : BroadcastReceiver
{
    public const string ActionTask = "app.mosaic.mobile.action.TASK";

    public const string ActionRefresh = "app.mosaic.mobile.action.REFRESH";

    /// <summary>
    /// Marks a read-only row. Every list row must carry a fill-in intent, because
    /// a recycled row otherwise inherits the previous row's task id and a tap
    /// would complete the wrong task. A read-only row sends its own id plus this
    /// flag, and the receiver returns before any write when the flag is set.
    /// </summary>
    public const string ExtraReadOnly = "readOnly";

    public const string ExtraTaskId = "taskId";

    public const string ExtraAction = "action";

    public const string ExtraWidgetId = "widgetId";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
        {
            return;
        }

        var actionName = intent.Action;
        var refresh = string.Equals(actionName, ActionRefresh, StringComparison.Ordinal);
        if (!refresh && !string.Equals(actionName, ActionTask, StringComparison.Ordinal))
        {
            return;
        }

        if (!refresh && intent.GetBooleanExtra(ExtraReadOnly, false))
        {
            // A completed or archived row. It is a no-op rather than a write
            // the kernel would refuse.
            return;
        }

        if (!CardActionCodec.TryParse(intent.GetStringExtra(ExtraAction), out var action))
        {
            WidgetRuntime.ShowToast(context, "不支持的操作");
            return;
        }

        var taskId = Guid.Empty;
        if (!refresh)
        {
            if (!Guid.TryParse(intent.GetStringExtra(ExtraTaskId), out taskId))
            {
                return;
            }
            if (action == CardAction.Refresh)
            {
                WidgetRuntime.ShowToast(context, "不支持的操作");
                return;
            }
        }
        else if (action != CardAction.Refresh)
        {
            WidgetRuntime.ShowToast(context, "不支持的操作");
            return;
        }

        var pending = GoAsync();
        var appContext = context;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = refresh
                    ? await WidgetRuntime.RefreshFromWidgetAsync(appContext).ConfigureAwait(false)
                    : await WidgetRuntime.ExecuteAsync(appContext, taskId, action).ConfigureAwait(false);
                WidgetRuntime.ShowToast(appContext, result.Message);
                WidgetRuntime.RenderAll(appContext, result.Model);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}