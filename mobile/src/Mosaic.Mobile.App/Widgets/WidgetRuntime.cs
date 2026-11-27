using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Commands;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Shared widget pipeline: run the kernel with a hard time budget, persist the
/// full snapshot the list factory cuts every group from, and redraw the widget
/// instances.
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
            WidgetSnapshotStore.Save(context, TestSeedStore.Items());
            return Remember(seeded);
        }
#endif
        try
        {
            using var budget = new CancellationTokenSource(WorkBudget);
            var refresh = await MobileServices.Kernel.RefreshWidgetAsync(budget.Token).ConfigureAwait(false);
            if (refresh.Items is { } items)
            {
                WidgetSnapshotStore.Save(context, items);
            }

            return Remember(refresh.Model);
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
            WidgetSnapshotStore.Save(context, TestSeedStore.Items());
            Remember(seeded.Model);
            WidgetRenderer.NotifyRowsChanged(context);
            return seeded;
        }
#endif
        try
        {
            using var budget = new CancellationTokenSource(WorkBudget);
            var result = await MobileServices.Kernel.ExecuteAsync(taskId, action, budget.Token).ConfigureAwait(false);
            if (result.WidgetItems is { } items)
            {
                WidgetSnapshotStore.Save(context, items);
            }

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

        foreach (var id in InstanceIds(context, manager))
        {
            WidgetRenderer.Render(context, manager, id, model);
        }
    }

    /// <summary>Redraws one instance after its group changed.</summary>
    public static void RenderOne(Context context, int appWidgetId)
    {
        ArgumentNullException.ThrowIfNull(context);

        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null)
        {
            return;
        }

        var model = _lastModel ?? MobileServices.Fallback(string.Empty);
        WidgetRenderer.Render(context, manager, appWidgetId, model);
    }

    public static int[] InstanceIds(Context context, AppWidgetManager manager)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);

        var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(TaskWidgetProvider))!);
        return manager.GetAppWidgetIds(provider) ?? [];
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

    private static CardModel Remember(CardModel model)
    {
        _lastModel = model;
        return model;
    }
}
