#if MOSAIC_WIDGET_TEST_SEED
using Android.App;
using Android.Content;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Test-build-only entry point:
/// <c>adb shell am broadcast -a app.mosaic.mobile.action.SEED_TEST</c>.
/// Exported so adb can reach it; the compiled release build has no such receiver.
/// </summary>
[BroadcastReceiver(Exported = true)]
[IntentFilter([ActionSeed])]
public sealed class TestSeedReceiver : BroadcastReceiver
{
    public const string ActionSeed = "app.mosaic.mobile.action.SEED_TEST";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null || !string.Equals(intent.Action, ActionSeed, StringComparison.Ordinal))
        {
            return;
        }

        TestSeedStore.Reset(context);
        var pending = GoAsync();
        _ = Task.Run(() =>
        {
            try
            {
                WidgetSnapshotStore.Save(context, TestSeedStore.Items());
                WidgetRuntime.RenderAll(context, TestSeedStore.Model());
                WidgetRenderer.NotifyRowsChanged(context);
                WidgetRuntime.ShowToast(context, "测试数据已生成");
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}
#endif