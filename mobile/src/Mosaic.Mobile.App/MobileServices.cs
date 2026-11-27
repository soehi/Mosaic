using Android.Content;
using Mosaic.Mobile.App.FluidCloud;
using Mosaic.Mobile.App.Platform;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Auth;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Configuration;

namespace Mosaic.Mobile.App;

/// <summary>
/// Process-wide composition root. It is created from
/// <see cref="MosaicApplication"/> so a widget broadcast that starts a cold
/// process still finds a fully wired kernel before the first render.
/// </summary>
public static class MobileServices
{
    private static readonly object Gate = new();
    private static MobileKernel? _kernel;
    private static AndroidFluidCloudAdapter? _fluidCloud;

    public static MobileKernel Kernel
    {
        get
        {
            lock (Gate)
            {
                return _kernel ?? throw new InvalidOperationException("Mobile services were not initialized.");
            }
        }
    }

    public static AndroidFluidCloudAdapter FluidCloud
    {
        get
        {
            lock (Gate)
            {
                return _fluidCloud ?? throw new InvalidOperationException("Mobile services were not initialized.");
            }
        }
    }

    public static void Initialize(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            if (_kernel is not null)
            {
                return;
            }

            var dataDirectory = context.FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("The app data directory is unavailable.");

            var secrets = new AndroidSecretStorage(context);
            var tokens = new TokenStore(secrets);
            var session = new SessionManager(new HttpMosaicApiFactory(), tokens);
            var config = new AndroidServerConfigProvider(context);
            _fluidCloud = new AndroidFluidCloudAdapter();
            _kernel = new MobileKernel(config, session, new HttpMosaicApiFactory(), dataDirectory, TimeProvider.System, _fluidCloud);
        }
    }

    public static CardModel Fallback(string detail) =>
        CardModelBuilder.Status(CardState.Offline, FluidCloud.Status, detail);
}