using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Android.Widget;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Binds a <see cref="CardModel"/> to the widget layout. The layout only shows a
/// count, the detail line, up to five titles and the two permitted actions; it
/// never contains an email, a token or a note.
/// </summary>
internal static class WidgetRenderer
{
    private static readonly int[] RowIds =
    [
        Resource.Id.row_1,
        Resource.Id.row_2,
        Resource.Id.row_3,
        Resource.Id.row_4,
        Resource.Id.row_5,
    ];

    private static readonly int[] TitleIds =
    [
        Resource.Id.title_1,
        Resource.Id.title_2,
        Resource.Id.title_3,
        Resource.Id.title_4,
        Resource.Id.title_5,
    ];

    private static readonly int[] CompleteIds =
    [
        Resource.Id.complete_1,
        Resource.Id.complete_2,
        Resource.Id.complete_3,
        Resource.Id.complete_4,
        Resource.Id.complete_5,
    ];

    private static readonly int[] ArchiveIds =
    [
        Resource.Id.archive_1,
        Resource.Id.archive_2,
        Resource.Id.archive_3,
        Resource.Id.archive_4,
        Resource.Id.archive_5,
    ];

    public static void Render(Context context, AppWidgetManager manager, int appWidgetId, CardModel model)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(model);

        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_tasks);
        views.SetTextViewText(Resource.Id.widget_headline, model.Headline);
        views.SetTextViewText(Resource.Id.widget_detail, model.Detail);
        views.SetTextViewText(Resource.Id.widget_refresh, context.GetString(Resource.String.action_refresh));

        var fluid = model.FluidCloud;
        if (fluid.IsAvailable)
        {
            views.SetViewVisibility(Resource.Id.widget_fluid, ViewStates.Gone);
        }
        else
        {
            views.SetTextViewText(Resource.Id.widget_fluid, fluid.Message);
            views.SetViewVisibility(Resource.Id.widget_fluid, ViewStates.Visible);
        }

        var visibleRows = ResolveRowCount(manager, appWidgetId);
        for (var index = 0; index < RowIds.Length; index++)
        {
            if (index >= visibleRows || index >= model.Rows.Count)
            {
                views.SetViewVisibility(RowIds[index], ViewStates.Gone);
                continue;
            }

            var task = model.Rows[index];
            views.SetViewVisibility(RowIds[index], ViewStates.Visible);
            views.SetTextViewText(TitleIds[index], task.IsCompleted ? "✓ " + task.Title : task.Title);

            if (task.IsCompleted)
            {
                views.SetViewVisibility(CompleteIds[index], ViewStates.Gone);
            }
            else
            {
                views.SetViewVisibility(CompleteIds[index], ViewStates.Visible);
                views.SetOnClickPendingIntent(
                    CompleteIds[index],
                    TaskIntent(context, task.Id, CardAction.Complete, appWidgetId));
            }

            views.SetOnClickPendingIntent(
                ArchiveIds[index],
                TaskIntent(context, task.Id, CardAction.Archive, appWidgetId));
        }

        views.SetOnClickPendingIntent(Resource.Id.widget_refresh, RefreshIntent(context, appWidgetId));

        manager.UpdateAppWidget(appWidgetId, views);
    }

    private static int ResolveRowCount(AppWidgetManager manager, int appWidgetId)
    {
        var options = manager.GetAppWidgetOptions(appWidgetId);
        var minHeight = options?.GetInt(AppWidgetManager.OptionAppwidgetMinHeight, 0) ?? 0;
        return minHeight switch
        {
            >= 220 => 5,
            >= 180 => 4,
            >= 140 => 3,
            >= 100 => 2,
            _ => 1,
        };
    }

    private static PendingIntent TaskIntent(Context context, Guid taskId, CardAction action, int appWidgetId)
    {
        var intent = new Intent(context, typeof(WidgetActionReceiver));
        intent.SetAction(WidgetActionReceiver.ActionTask);
        // PendingIntent identity ignores extras, so the task and action are also
        // encoded in the data URI; without it two tasks could share an intent.
        intent.SetData(Android.Net.Uri.Parse(
            "mosaic-widget://task/" + taskId.ToString("D") + "/" + CardActionCodec.ToWire(action)));
        intent.PutExtra(WidgetActionReceiver.ExtraTaskId, taskId.ToString("D"));
        intent.PutExtra(WidgetActionReceiver.ExtraAction, CardActionCodec.ToWire(action));
        intent.PutExtra(WidgetActionReceiver.ExtraWidgetId, appWidgetId);

        // Request code is derived from (taskId, action); FLAG_IMMUTABLE keeps the
        // extras unmodifiable by whoever receives the PendingIntent.
        var code = CardActionCodec.RequestCode(taskId, action);
        return PendingIntent.GetBroadcast(
                context,
                code,
                intent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("PendingIntent creation failed.");
    }

    private static PendingIntent RefreshIntent(Context context, int appWidgetId)
    {
        var intent = new Intent(context, typeof(WidgetActionReceiver));
        intent.SetAction(WidgetActionReceiver.ActionRefresh);
        intent.SetData(Android.Net.Uri.Parse("mosaic-widget://refresh"));
        intent.PutExtra(WidgetActionReceiver.ExtraAction, CardActionCodec.RefreshWire);
        intent.PutExtra(WidgetActionReceiver.ExtraWidgetId, appWidgetId);
        return PendingIntent.GetBroadcast(
                context,
                RefreshRequestCode,
                intent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("PendingIntent creation failed.");
    }

    private const int RefreshRequestCode = 0x4D4F5341;
}