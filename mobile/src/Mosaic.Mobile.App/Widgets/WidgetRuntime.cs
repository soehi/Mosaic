using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Commands;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Shared widget pipeline: run the kernel with a hard time budget, persist the
/// row snapshot the list factory reads, and redraw every widget instance.
/// </summary>
internal static class WidgetRuntime
{
    private static readonly TimeSpan WorkBudget = TimeSpan.FromSeconds(8);

    private static CardModel? _lastModel;

    public static CardModel? LastModel => _lastModel;

    public static async Task<CardModel> RefreshAsync(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
#if MOSAIC_WIDGET_TEST_SEED
        if (TestSeedStore.IsSeeded)
        {
            var seeded = TestSeedStore.Model();
            WidgetSnapshotStore.Save(context, TestSeedStore.Rows());
            return Remember(seeded);
        }
#endif
        try
        {
            using var budget = new CancellationTokenSource(WorkBudget);
            var model = await MobileServices.Kernel.RefreshAsync(budget.Token).ConfigureAwait(false);
            WidgetSnapshotStore.Save(context, ToRows(model));
            return Remember(model);
        }
        catch (OperationCanceledException)
        {
            return Remember(MobileServices.Fallback("同步超时，请稍后刷新"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Remember(MobileServices.Fallback("同步失败，请稍后刷新"));
        }
    }

    public static async Task<CardActionResult> ExecuteAsync(Context context, Guid taskId, CardAction action)
    {
        ArgumentNullException.ThrowIfNull(context);
#if MOSAIC_WIDGET_TEST_SEED
        if (TestSeedStore.IsSeeded)
        {
            var seeded = TestSeedStore.Apply(taskId, action);
            WidgetSnapshotStore.Save(context, TestSeedStore.Rows());
            Remember(seeded.Model);
            WidgetRenderer.NotifyRowsChanged(context);
            return seeded;
        }
#endif
        try
        {
            using var budget = new CancellationTokenSource(WorkBudget);
            var result = await MobileServices.Kernel.ExecuteAsync(taskId, action, budget.Token).ConfigureAwait(false);
            WidgetSnapshotStore.Save(context, ToRows(result.Model));
            Remember(result.Model);
            WidgetRenderer.NotifyRowsChanged(context);
            return result;
        }
        catch (OperationCanceledException)
        {
            var fallback = Remember(MobileServices.Fallback("操作超时，未提交"));
            return new CardActionResult(CommandOutcome.Cancelled, "操作超时，未提交", fallback);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var fallback = Remember(MobileServices.Fallback("操作失败，请稍后刷新"));
            return new CardActionResult(CommandOutcome.ServerError, "操作失败，请稍后刷新", fallback);
        }
    }

    public static void RenderAll(Context context, CardModel model)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);

        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null)
        {
            return;
        }

        var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(TaskWidgetProvider))!);
        var ids = manager.GetAppWidgetIds(provider);
        if (ids is null)
        {
            return;
        }

        foreach (var id in ids)
        {
            WidgetRenderer.Render(context, manager, id, model);
        }
    }

    public static void ShowToast(Context context, string message)
    {
        try
        {
            Toast.MakeText(context, message, ToastLength.Short)?.Show();
        }
        catch (Exception exception) when (exception is Java.Lang.Exception)
        {
            // A missing toast never breaks the widget update.
        }
    }

    /// <summary>Only unfinished tasks appear in the list.</summary>
    private static IReadOnlyList<WidgetRow> ToRows(CardModel model) =>
        model.Rows
            .Where(row => !row.IsCompleted)
            .Select(row => new WidgetRow(row.Id.ToString("D"), row.Title, row.IsCompleted))
            .ToList();

    private static CardModel Remember(CardModel model)
    {
        _lastModel = model;
        return model;
    }
}