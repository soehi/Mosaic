using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Util;
using Android.Views;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Binds a <see cref="CardModel"/> to the widget. The visible surface is a
/// single <see cref="Android.Widget.ListView"/> fed by
/// <see cref="TaskWidgetService"/>: rows come from the list factory, so the
/// widget scrolls natively and the only action is the per-row complete circle.
///
/// Rendering is defensive: a launcher that rejects the layout degrades to
/// <c>widget_tasks_safe</c>, which is TextView-only and has no actions.
/// </summary>
internal static class WidgetRenderer
{
    public static void Render(Context context, AppWidgetManager manager, int appWidgetId, CardModel model)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(model);

        try
        {
            Log.Info(WidgetDiagnostics.Tag, "Render start for id=" + appWidgetId);
            var views = WidgetViewport.ForHost(context, manager, appWidgetId,
                viewport => BuildViews(context, appWidgetId, viewport));
#pragma warning disable CA1422
            manager.UpdateAppWidget(appWidgetId, views);
            manager.NotifyAppWidgetViewDataChanged(appWidgetId, Resource.Id.widget_list);
#pragma warning restore CA1422
            Log.Info(WidgetDiagnostics.Tag, "Render done for id=" + appWidgetId);
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or InvalidOperationException or ArgumentException)
        {
            Log.Warn(WidgetDiagnostics.Tag, "Render failed, falling back: " + exception.Message);
            RenderSafe(context, manager, appWidgetId, model);
        }
    }

    private static RemoteViews BuildViews(Context context, int appWidgetId, WidgetViewport viewport)
    {
        var views = new RemoteViews(context.PackageName, viewport.Rows == 2 ? Resource.Layout.widget_tasks_tall : Resource.Layout.widget_tasks);
        views.SetViewVisibility(Resource.Id.widget_mark, viewport.ShowMark ? ViewStates.Visible : ViewStates.Gone);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            views.SetViewLayoutHeight(Resource.Id.widget_list, viewport.Rows * viewport.RowHeightPx, (int)ComplexUnitType.Px);
        var adapter = new Intent(context, typeof(TaskWidgetService));
        adapter.PutExtra(AppWidgetManager.ExtraAppwidgetId, appWidgetId);
        adapter.PutExtra(WidgetViewport.RowHeightExtra, viewport.RowHeightPx);
        adapter.PutExtra(WidgetViewport.MarkExtra, viewport.ShowMark);
        adapter.SetData(Android.Net.Uri.Parse($"mosaic-widget://rows/{appWidgetId}/{viewport.RowHeightPx}/{viewport.ShowMark}"));
#pragma warning disable CA1422
        views.SetRemoteAdapter(Resource.Id.widget_list, adapter);
        views.SetPendingIntentTemplate(Resource.Id.widget_list, TaskTemplate(context, appWidgetId));
#pragma warning restore CA1422
        views.SetOnClickPendingIntent(Resource.Id.widget_refresh, RefreshIntent(context, appWidgetId));
        ApplyRefreshProgress(views, RefreshInProgress(appWidgetId));
        return views;
    }

    private static readonly HashSet<int> Refreshing = [];
    private static readonly object RefreshGate = new();

    /// <summary>
    /// RemoteViews cannot run an animator from a broadcast, so the visible
    /// spinner uses an animated drawable and is toggled by visibility. The
    /// refresh button stays clickable but the receiver ignores duplicate work.
    /// </summary>
    public static void SetRefreshing(Context context, int appWidgetId, bool refreshing)
    {
        lock (RefreshGate)
        {
            if (refreshing) Refreshing.Add(appWidgetId);
            else Refreshing.Remove(appWidgetId);
        }

        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(TaskWidgetProvider))!));
        if (ids is null || Array.IndexOf(ids, appWidgetId) < 0) return;
        var options = manager.GetAppWidgetOptions(appWidgetId);
        var density = context.Resources!.DisplayMetrics!.Density;
        var rows = WidgetRowGeometry.ForHostSize(
            options?.GetInt(AppWidgetManager.OptionAppwidgetMinHeight, 0) ?? 110, density).Rows;
        var views = new RemoteViews(context.PackageName,
            rows == 2 ? Resource.Layout.widget_tasks_tall : Resource.Layout.widget_tasks);
        ApplyRefreshProgress(views, refreshing);
#pragma warning disable CA1422
        manager.PartiallyUpdateAppWidget(appWidgetId, views);
#pragma warning restore CA1422
    }

    public static void ClearRefreshing(int appWidgetId)
    {
        lock (RefreshGate) Refreshing.Remove(appWidgetId);
    }

    private static bool RefreshInProgress(int appWidgetId)
    {
        lock (RefreshGate) return Refreshing.Contains(appWidgetId);
    }

    private static void ApplyRefreshProgress(RemoteViews views, bool refreshing)
    {
        views.SetViewVisibility(Resource.Id.widget_refresh_animation,
            refreshing ? ViewStates.Visible : ViewStates.Gone);
        views.SetViewVisibility(Resource.Id.widget_refresh,
            refreshing ? ViewStates.Gone : ViewStates.Visible);
    }

    /// <summary>Asks every instance of this provider to rebuild its list rows.</summary>
    public static void NotifyRowsChanged(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null)
        {
            return;
        }

        var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(TaskWidgetProvider))!);
        var ids = manager.GetAppWidgetIds(provider);
        if (ids is { Length: > 0 })
        {
#pragma warning disable CA1422
            manager.NotifyAppWidgetViewDataChanged(ids, Resource.Id.widget_list);
#pragma warning restore CA1422
        }
    }

    private static void RenderSafe(Context context, AppWidgetManager manager, int appWidgetId, CardModel model)
    {
        try
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.widget_tasks_safe);
            views.SetTextViewText(Resource.Id.safe_headline, model.Headline);
            views.SetTextViewText(Resource.Id.safe_detail, model.Detail);
            manager.UpdateAppWidget(appWidgetId, views);
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or InvalidOperationException or ArgumentException)
        {
            // Nothing else can be rendered; the launcher keeps the previous frame.
        }
    }

    /// <summary>
    /// One template for every row. The row's fill-in intent supplies the task id;
    /// the merged intent carries this template's action and component, so a click
    /// cannot reach anything but the complete command.
    /// </summary>
    private static PendingIntent TaskTemplate(Context context, int appWidgetId)
    {
        var intent = new Intent(context, typeof(WidgetActionReceiver));
        intent.SetAction(WidgetActionReceiver.ActionTask);
        intent.SetData(Android.Net.Uri.Parse("mosaic-widget://complete/" + appWidgetId));
        intent.PutExtra(WidgetActionReceiver.ExtraAction, CardActionCodec.CompleteWire);
        intent.PutExtra(WidgetActionReceiver.ExtraWidgetId, appWidgetId);

        // A widget list template must be MUTABLE so the launcher can fill in each
        // row's extras; FLAG_IMMUTABLE is only the default from API 31 onward, and
        // on API 26-30 the flag does not exist. The intent is explicit (our
        // component and action) and the receiver is not exported, so the template
        // cannot be used to reach anything else.
        var flags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            flags |= PendingIntentFlags.Mutable;
        }

        return PendingIntent.GetBroadcast(context, TaskTemplateRequestCode(appWidgetId), intent, flags)
            ?? throw new InvalidOperationException("PendingIntent creation failed.");
    }

    private static int TaskTemplateRequestCode(int appWidgetId) => 0x4D4F3000 + (appWidgetId & 0x0FFF);

    /// <summary>
    /// A standalone, immutable refresh intent. It carries no task id and goes
    /// through the same non-exported receiver, so a crafted click cannot write
    /// a task or reuse a row's completion intent. The button also shows a
    /// spinner during the bounded refresh; no spinner state is stored on disk.
    /// </summary>
    internal static PendingIntent RefreshIntent(Context context, int appWidgetId)
    {
        var intent = new Intent(context, typeof(WidgetActionReceiver));
        intent.SetAction(WidgetActionReceiver.ActionRefresh);
        intent.SetData(Android.Net.Uri.Parse("mosaic-widget://refresh/" + appWidgetId));
        intent.PutExtra(WidgetActionReceiver.ExtraAction, CardActionCodec.RefreshWire);
        intent.PutExtra(WidgetActionReceiver.ExtraWidgetId, appWidgetId);
        var flags = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
        return PendingIntent.GetBroadcast(context, RefreshRequestCode(appWidgetId), intent, flags)
            ?? throw new InvalidOperationException("PendingIntent creation failed.");
    }

    internal static int RefreshRequestCode(int appWidgetId) => 0x4D4F4000 + (appWidgetId & 0x0FFF);
}