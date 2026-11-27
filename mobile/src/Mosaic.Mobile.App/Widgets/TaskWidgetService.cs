using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Backs the widget list with a native <see cref="ListView"/>. This is the only
/// supported way to give an app widget a real vertical scroll gesture: plain
/// RemoteViews cannot receive touch events, so a custom swipe would have to be
/// faked with clickable areas and would not scroll.
/// </summary>
[Service(Exported = false, Permission = "android.permission.BIND_REMOTEVIEWS")]
public sealed class TaskWidgetService : RemoteViewsService
{
    public override IRemoteViewsFactory OnGetViewFactory(Intent? intent)
    {
        var appWidgetId = intent?.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId)
            ?? AppWidgetManager.InvalidAppwidgetId;
        return new TaskRowFactory(ApplicationContext ?? this, appWidgetId);
    }
}

/// <summary>Builds the rows for this widget's configured group.</summary>
internal sealed class TaskRowFactory : Java.Lang.Object, RemoteViewsService.IRemoteViewsFactory
{
    private readonly Context _context;

    private IReadOnlyList<WidgetRow> _rows = [];

    private WidgetTaskGroup _group = WidgetTaskGroup.Pending;

    public TaskRowFactory(Context context, int appWidgetId)
    {
        _context = context;
        AppWidgetId = appWidgetId;
    }

    public int AppWidgetId { get; }

    public int Count => _rows.Count == 0 ? 1 : _rows.Count;

    public bool HasStableIds => true;

    public void OnCreate()
    {
    }

    public void OnDataSetChanged()
    {
        _group = WidgetGroupStore.Get(_context, AppWidgetId);
        _rows = LoadRows(_context, _group);
    }

    public void OnDestroy()
    {
    }

    public RemoteViews? GetViewAt(int position)
    {
        if (_rows.Count == 0)
        {
            var empty = new RemoteViews(_context.PackageName, Resource.Layout.widget_task_empty);
            empty.SetTextViewText(Resource.Id.widget_empty_text, WidgetTaskGroups.EmptyText(_group));
            return empty;
        }

        if (position < 0 || position >= _rows.Count)
        {
            return null;
        }

        var row = _rows[position];
        var views = new RemoteViews(_context.PackageName, Resource.Layout.widget_task_row);
        views.SetTextViewText(Resource.Id.widget_row_title, row.Title);
        views.SetImageViewResource(
            Resource.Id.widget_row_circle,
            row.IsCompleted ? Resource.Drawable.widget_circle_checked : Resource.Drawable.widget_circle_outline);

        // Every row carries a fill-in intent with all three keys set, including
        // the read-only ones. A row that simply omitted the click intent let a
        // recycled row's intent through, and a tap on a completed row then
        // completed a different, still-pending task. Read-only rows therefore
        // send their own id plus an explicit flag the receiver drops, so neither
        // a stale id nor a stale flag can reach a write.
        var writable = !row.IsCompleted && !row.IsArchived && WidgetTaskGroups.AllowsComplete(_group);
        var fillIn = new Intent();
        fillIn.SetAction(WidgetActionReceiver.ActionTask);
        fillIn.PutExtra(WidgetActionReceiver.ExtraTaskId, row.Id);
        fillIn.PutExtra(WidgetActionReceiver.ExtraAction, CardActionCodec.CompleteWire);
        fillIn.PutExtra(WidgetActionReceiver.ExtraReadOnly, !writable);

        views.SetOnClickFillInIntent(Resource.Id.widget_row_circle, fillIn);
        return views;
    }

    public RemoteViews? LoadingView => null;

    public int ViewTypeCount => 2;

    public long GetItemId(int position) => position;

    private static IReadOnlyList<WidgetRow> LoadRows(Context context, WidgetTaskGroup group)
    {
        var source = WidgetSnapshotStore.LoadItems(context);
#if MOSAIC_WIDGET_TEST_SEED
        if (TestSeedStore.IsSeeded)
        {
            source = TestSeedStore.Items();
        }
#endif
        return WidgetTaskSelector
            .Select(source, group)
            .Select(WidgetRowFactory.ToRow)
            .ToList();
    }
}
