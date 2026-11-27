using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Commands;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Shared widget pipeline: run the kernel with a hard time budget, remember the
/// last card for cheap re-renders on resize, and redraw every widget instance.
/// </summary>
internal static class WidgetRuntime
{
    private static readonly TimeSpan WorkBudget = TimeSpan.FromSeconds(8);

    private static CardModel? _lastModel;

    public static CardModel? LastModel => _lastModel;

    public static async Task<CardModel> RefreshAsync()
    {
#if MOSAIC_WIDGET_TEST_SEED
        if (TestSeedStore.IsSeeded)
        {
            return Remember(TestSeedStore.Model());
        }
#endif
        try
        {
            using var budget = new CancellationTokenSource(WorkBudget);
            return Remember(await MobileServices.Kernel.RefreshAsync(budget.Token).ConfigureAwait(false));
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

    public static async Task<CardActionResult> ExecuteAsync(Guid taskId, CardAction action)
    {
#if MOSAIC_WIDGET_TEST_SEED
        if (TestSeedStore.IsSeeded)
        {
            var seeded = TestSeedStore.Apply(taskId, action);
            Remember(seeded.Model);
            return seeded;
        }
#endif
        try
        {
            using var budget = new CancellationTokenSource(WorkBudget);
            var result = await MobileServices.Kernel.ExecuteAsync(taskId, action, budget.Token).ConfigureAwait(false);
            Remember(result.Model);
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

    public static void RenderOne(Context context, int appWidgetId)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null)
        {
            return;
        }

        WidgetRenderer.Render(context, manager, appWidgetId, _lastModel ?? MobileServices.Fallback("正在同步"));
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