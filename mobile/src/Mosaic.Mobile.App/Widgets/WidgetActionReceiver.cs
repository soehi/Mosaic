using Android.App;
using Android.Content;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// The widget's only broadcast surface. It accepts the three permitted actions
/// and nothing else, and it is not exported, so no other app can invoke it.
///
/// A stale PendingIntent is harmless across account switches: the command looks
/// the task id up inside the current account's projection, so an id from a
/// previous account simply returns "任务不在本地投影中".
/// </summary>
[BroadcastReceiver(Exported = false)]
[IntentFilter([ActionTask, ActionRefresh])]
public sealed class WidgetActionReceiver : BroadcastReceiver
{
    public const string ActionTask = "app.mosaic.mobile.action.TASK";

    public const string ActionRefresh = "app.mosaic.mobile.action.REFRESH";

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
        if (!string.Equals(actionName, ActionTask, StringComparison.Ordinal)
            && !string.Equals(actionName, ActionRefresh, StringComparison.Ordinal))
        {
            return;
        }

        var rawAction = intent.GetStringExtra(ExtraAction)
            ?? (string.Equals(actionName, ActionRefresh, StringComparison.Ordinal) ? CardActionCodec.RefreshWire : null);
        if (!CardActionCodec.TryParse(rawAction, out var action))
        {
            WidgetRuntime.ShowToast(context, "不支持的操作");
            return;
        }

        Guid taskId = Guid.Empty;
        if (action != CardAction.Refresh && !Guid.TryParse(intent.GetStringExtra(ExtraTaskId), out taskId))
        {
            return;
        }

        var pending = GoAsync();
        var appContext = context;
        _ = Task.Run(async () =>
        {
            try
            {
                if (action == CardAction.Refresh)
                {
                    var model = await WidgetRuntime.RefreshAsync().ConfigureAwait(false);
                    WidgetRuntime.RenderAll(appContext, model);
                }
                else
                {
                    var result = await WidgetRuntime.ExecuteAsync(taskId, action).ConfigureAwait(false);
                    WidgetRuntime.ShowToast(appContext, result.Message);
                    WidgetRuntime.RenderAll(appContext, result.Model);
                }
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}