using Android.App;
using Android.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Mosaic.Mobile.App.Platforms.Android.Widgets;

namespace Mosaic.Mobile.App;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        // Subscribe from the Application, independent of MainActivity/AppShell,
        // so sync completion and auth changes refresh widgets even when the app
        // UI was never opened in this process.
        IPlatformApplication.Current?.Services
            .GetRequiredService<WidgetRefresh>()
            .Subscribe();
    }
}
