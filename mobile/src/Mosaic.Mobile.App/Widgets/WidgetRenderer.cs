using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Views;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Binds a <see cref="CardModel"/> to the widget layout. The layout only shows a
/// count, the detail line, up to five titles and the two permitted actions; it
/// never contains an email, a token or a note.
///
/// Rendering is defensive: a launcher that rejects the layout (for example an
/// unsupported view type) degrades to <c>widget_tasks_safe</c>, which contains
/// nothing but TextViews and the refresh action, instead of leaving the user
/// with "载入窗口小部件出现问题".
/// </summary>
internal static class WidgetRenderer
{
    /// <summary>Rows below this width use one-character labels so both actions stay on the row.</summary>
    private const int CompactWidthDp = 180;

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

        try
        {
            var views = BuildViews(context, manager, appWidgetId, model);
            manager.UpdateAppWidget(appWidgetId, views);
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or InvalidOperationException or ArgumentException)
        {
            // The host rejected the layout; fall back to a whitelist-only layout.
            RenderSafe(context, manager, appWidgetId, model);
        }
    }

    private static RemoteViews BuildViews(Context context, AppWidgetManager manager, int appWidgetId, CardModel model)
    {
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

        var options = manager.GetAppWidgetOptions(appWidgetId);
        var visibleRows = ResolveRowCount(options);
        var compact = UseCompactLabels(options);
        var completeText = context.GetString(compact
            ? Resource.String.action_complete_compact
            : Resource.String.action_complete);
        var archiveText = context.GetString(compact
            ? Resource.String.action_archive_compact
            : Resource.String.action_archive);

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
            views.SetTextViewText(ArchiveIds[index], archiveText);

            if (task.IsCompleted)
            {
                views.SetViewVisibility(CompleteIds[index], ViewStates.Gone);
            }
            else
            {
                views.SetViewVisibility(CompleteIds[index], ViewStates.Visible);
                views.SetTextViewText(CompleteIds[index], completeText);
                views.SetOnClickPendingIntent(
                    CompleteIds[index],
                    TaskIntent(context, task.Id, CardAction.Complete, appWidgetId));
            }

            views.SetOnClickPendingIntent(
                ArchiveIds[index],
                TaskIntent(context, task.Id, CardAction.Archive, appWidgetId));
        }

        views.SetOnClickPendingIntent(Resource.Id.widget_refresh, RefreshIntent(context, appWidgetId));
        return views;
    }

    private static void RenderSafe(Context context, AppWidgetManager manager, int appWidgetId, CardModel model)
    {
        try
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.widget_tasks_safe);
            views.SetTextViewText(Resource.Id.safe_headline, model.Headline);
            views.SetTextViewText(Resource.Id.safe_detail, model.Detail);
            views.SetTextViewText(Resource.Id.safe_refresh, context.GetString(Resource.String.action_refresh));
            views.SetOnClickPendingIntent(Resource.Id.safe_refresh, RefreshIntent(context, appWidgetId));
            manager.UpdateAppWidget(appWidgetId, views);
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or InvalidOperationException or ArgumentException)
        {
            // Nothing else can be rendered; the launcher keeps the previous frame.
        }
    }

    /// <summary>
    /// Rows that fit the current height. The maximum height is authoritative for
    /// the size in use; the minimum is only a fallback for hosts that omit it.
    /// A 4x2 cell (~110dp) shows two rows, a taller cell up to five.
    /// </summary>
    private static int ResolveRowCount(Bundle? options)
    {
        // AppWidgetOptions sizes are pixels, not dp. Convert with the display
        // density so a 4x2 cell (~110dp) always lands in the two-row bucket.
        var density = Android.Content.Res.Resources.System?.DisplayMetrics?.Density ?? 1f;
        if (density <= 0f)
        {
            density = 1f;
        }

        var heightPixels = ReadOption(options, AppWidgetManager.OptionAppwidgetMaxHeight);
        if (heightPixels <= 0)
        {
            heightPixels = ReadOption(options, AppWidgetManager.OptionAppwidgetMinHeight);
        }

        var height = heightPixels / density;
        return height switch
        {
            >= 220 => 5,
            >= 170 => 4,
            >= 140 => 3,
            >= 100 => 2,
            > 0 => 1,
            _ => 2,
        };
    }

    private static bool UseCompactLabels(Bundle? options)
    {
        var width = ReadOption(options, AppWidgetManager.OptionAppwidgetMinWidth);
        return width <= 0 || width < CompactWidthDp;
    }

    private static int ReadOption(Bundle? options, string key) =>
        options?.GetInt(key, 0) ?? 0;

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