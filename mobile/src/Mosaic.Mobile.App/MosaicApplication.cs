using Android.App;
using Android.Runtime;
using Mosaic.Mobile.App.Platform;

namespace Mosaic.Mobile.App;

/// <summary>
/// The only entry point of the app. There is no Activity: every surface is a
/// system AppWidget or a broadcast from one, so the process is started by the
/// system and the kernel is composed here.
/// </summary>
[Application]
public sealed class MosaicApplication : Application
{
    public MosaicApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        MobileServices.Initialize(this);
        LegacyInstallCleanup.Run(this);
    }
}