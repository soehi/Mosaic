using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.App.Platforms.Android.Widgets;

/// <summary>
/// Builds one RemoteViews tree per widget instance. Every interactive row gets
/// an immutable PendingIntent that carries only taskId, action and appWidgetId —
/// never credentials, an email address or a task payload.
/// </summary>
internal static class WidgetFormatter
{
    internal const string ActionRefresh = "app.mosaic.tasks.widget.REFRESH";
    internal const string ActionTask = "app.mosaic.tasks.widget.TASK";
    internal const string ExtraTaskId = "taskId";
    internal const string ExtraAction = "taskAction";
    internal const string ExtraWidgetId = "widgetId";

    internal static RemoteViews Build(
        Context context,
        WidgetSnapshot snapshot,
        int appWidgetId,
        int maxRows,
        bool large,
        string? notice,
        WidgetTaskRepository repository)
    {
        var views = new RemoteViews(context.PackageName!,
            large ? Resource.Layout.widget_tasks_large : Resource.Layout.widget_tasks_small);

        var title = repository.Text(StringKeys.WidgetTitle);
        views.SetTextViewText(Resource.Id.widget_title, title);
        views.SetContentDescription(Resource.Id.widget_root, title);
        views.SetOnClickPendingIntent(Resource.Id.widget_root, RefreshIntent(context, appWidgetId));

        if (snapshot.BlockedReason != WidgetBlockedReason.None)
        {
            views.SetTextViewText(Resource.Id.widget_count, string.Empty);
            views.SetViewVisibility(Resource.Id.widget_count, ViewStates.Gone);
            views.SetTextViewText(Resource.Id.widget_more, string.Empty);
            views.SetViewVisibility(Resource.Id.widget_more, ViewStates.Gone);
            views.SetTextViewText(
                Resource.Id.widget_notice,
                repository.Text(BlockedKey(snapshot.BlockedReason)));
            views.SetViewVisibility(Resource.Id.widget_notice, ViewStates.Visible);
            ApplyRows(views, context, Array.Empty<WidgetTaskLine>(), appWidgetId, repository);
            ApplyNotice(views, notice);
            return views;
        }

        views.SetTextViewText(
            Resource.Id.widget_count,
            repository.Format(StringKeys.WidgetCount, snapshot.OpenCount));
        views.SetViewVisibility(Resource.Id.widget_count, ViewStates.Visible);
        var hidden = snapshot.OpenCount - snapshot.Tasks.Count;
        if (hidden > 0)
        {
            views.SetTextViewText(
                Resource.Id.widget_more,
                repository.Format(StringKeys.WidgetMore, hidden));
            views.SetViewVisibility(Resource.Id.widget_more, ViewStates.Visible);
        }
        else
        {
            views.SetTextViewText(Resource.Id.widget_more, string.Empty);
            views.SetViewVisibility(Resource.Id.widget_more, ViewStates.Gone);
        }
        views.SetTextViewText(Resource.Id.widget_notice, string.Empty);
        views.SetViewVisibility(Resource.Id.widget_notice, ViewStates.Gone);
        ApplyRows(views, context, snapshot.Tasks, appWidgetId, repository);
        ApplyNotice(views, notice);
        return views;
    }

    internal static int MaxRows(Bundle? options)
    {
        if (options is null || !options.ContainsKey(AppWidgetManager.OptionAppwidgetMinWidth))
        {
            return 1;
        }

        var widthDp = options.GetInt(AppWidgetManager.OptionAppwidgetMinWidth, 0);
        var heightDp = options.GetInt(AppWidgetManager.OptionAppwidgetMinHeight, 0);
        return widthDp >= 320 && heightDp >= 180 ? 5 : 1;
    }

    internal static PendingIntent RefreshIntent(Context context, int appWidgetId)
    {
        var intent = new Intent(context, typeof(TaskWidgetActionReceiver))
            .SetAction(ActionRefresh)
            .PutExtra(ExtraWidgetId, appWidgetId);
        return PendingIntent.GetBroadcast(
            context,
            appWidgetId * 10,
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    internal static PendingIntent TaskIntent(
        Context context,
        Guid taskId,
        WidgetTaskAction action,
        int appWidgetId,
        int requestCode)
    {
        var intent = new Intent(context, typeof(TaskWidgetActionReceiver))
            .SetAction(ActionTask)
            .PutExtra(ExtraTaskId, taskId.ToString("D"))
            .PutExtra(ExtraAction, (int)action)
            .PutExtra(ExtraWidgetId, appWidgetId);
        return PendingIntent.GetBroadcast(
            context,
            requestCode,
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private static void ApplyNotice(RemoteViews views, string? notice)
    {
        if (string.IsNullOrEmpty(notice))
        {
            views.SetTextViewText(Resource.Id.widget_status, string.Empty);
            views.SetViewVisibility(Resource.Id.widget_status, ViewStates.Gone);
            return;
        }

        views.SetTextViewText(Resource.Id.widget_status, notice);
        views.SetViewVisibility(Resource.Id.widget_status, ViewStates.Visible);
    }

    private static void ApplyRows(
        RemoteViews views,
        Context context,
        IReadOnlyList<WidgetTaskLine> tasks,
        int appWidgetId,
        WidgetTaskRepository repository)
    {
        for (var row = 0; row < 5; row++)
        {
            var root = RowId(row);
            var label = LabelId(row);
            var complete = CompleteId(row);
            var archive = ArchiveId(row);
            var restore = RestoreId(row);

            if (row >= tasks.Count)
            {
                views.SetViewVisibility(root, ViewStates.Gone);
                continue;
            }

            var task = tasks[row];
            views.SetViewVisibility(root, ViewStates.Visible);
            views.SetTextViewText(label, task.Title);
            views.SetContentDescription(label, task.Title);

            var completeText = repository.Text(StringKeys.WidgetComplete);
            var archiveText = repository.Text(StringKeys.WidgetArchive);
            var restoreText = repository.Text(StringKeys.WidgetRestore);

            views.SetTextViewText(complete, completeText);
            views.SetTextViewText(archive, archiveText);
            views.SetTextViewText(restore, restoreText);
            views.SetOnClickPendingIntent(
                complete,
                TaskIntent(context, task.Id, WidgetTaskAction.Complete, appWidgetId, appWidgetId * 100 + row * 3));
            views.SetOnClickPendingIntent(
                archive,
                TaskIntent(context, task.Id, WidgetTaskAction.Archive, appWidgetId, appWidgetId * 100 + row * 3 + 1));
            views.SetOnClickPendingIntent(
                restore,
                TaskIntent(context, task.Id, WidgetTaskAction.Restore, appWidgetId, appWidgetId * 100 + row * 3 + 2));
            views.SetViewVisibility(complete, ViewStates.Visible);
            views.SetViewVisibility(archive, ViewStates.Visible);
            views.SetViewVisibility(restore, ViewStates.Visible);
        }
    }

    private static string BlockedKey(WidgetBlockedReason reason) => reason switch
    {
        WidgetBlockedReason.NeedsSignIn => StringKeys.WidgetNeedsSignIn,
        WidgetBlockedReason.NeedsSync => StringKeys.WidgetNeedsSync,
        _ => StringKeys.WidgetUnavailable,
    };

    private static int RowId(int row) => row switch
    {
        0 => Resource.Id.widget_row_0,
        1 => Resource.Id.widget_row_1,
        2 => Resource.Id.widget_row_2,
        3 => Resource.Id.widget_row_3,
        _ => Resource.Id.widget_row_4,
    };

    private static int LabelId(int row) => row switch
    {
        0 => Resource.Id.widget_task_0,
        1 => Resource.Id.widget_task_1,
        2 => Resource.Id.widget_task_2,
        3 => Resource.Id.widget_task_3,
        _ => Resource.Id.widget_task_4,
    };

    private static int CompleteId(int row) => row switch
    {
        0 => Resource.Id.widget_complete_0,
        1 => Resource.Id.widget_complete_1,
        2 => Resource.Id.widget_complete_2,
        3 => Resource.Id.widget_complete_3,
        _ => Resource.Id.widget_complete_4,
    };

    private static int ArchiveId(int row) => row switch
    {
        0 => Resource.Id.widget_archive_0,
        1 => Resource.Id.widget_archive_1,
        2 => Resource.Id.widget_archive_2,
        3 => Resource.Id.widget_archive_3,
        _ => Resource.Id.widget_archive_4,
    };

    private static int RestoreId(int row) => row switch
    {
        0 => Resource.Id.widget_restore_0,
        1 => Resource.Id.widget_restore_1,
        2 => Resource.Id.widget_restore_2,
        3 => Resource.Id.widget_restore_3,
        _ => Resource.Id.widget_restore_4,
    };
}
