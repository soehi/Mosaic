using Microsoft.Extensions.Logging;
using Mosaic.Mobile.App.Pages;
using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Sync;
#if ANDROID
using Mosaic.Mobile.App.Platforms.Android.Widgets;
#endif

namespace Mosaic.Mobile.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<MosaicApp>();
#if DEBUG
        builder.Logging.AddDebug();
#endif

        ConfigureDeviceFacts();
        ConfigureServices(builder.Services);
        return builder.Build();
    }

    private static void ConfigureDeviceFacts()
    {
        var version = "0.2.1";
        try
        {
            version = AppInfo.Current.VersionString;
        }
        catch (Exception)
        {
            // AppInfo is unavailable in some design-time hosts; the fallback is
            // the assembly version we ship.
        }

        MobileDeviceInfo.AppVersion = version;
        MobileDeviceInfo.Platform = DeviceInfo.Platform == DevicePlatform.Android
            ? "android"
            : DeviceInfo.Platform == DevicePlatform.iOS ? "ios" : "mobile";
        MobileDeviceInfo.DeviceName = string.IsNullOrWhiteSpace(DeviceInfo.Name)
            ? "phone"
            : DeviceInfo.Name;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<AppPreferences>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<AppThemeService>();
        services.AddSingleton<ISecretStorage>(_ => CreateSecretStorage());
        services.AddSingleton(sp => new TokenStore(sp.GetRequiredService<ISecretStorage>()));

        var clientInfo = new MobileClientInfo(
            MobileDeviceInfo.AppVersion,
            MobileDeviceInfo.Platform,
            DeviceInfo.VersionString);
        services.AddSingleton(_ => new MosaicApiClient(MosaicApiClient.CreateHttpClient(clientInfo)));
        services.AddSingleton<IMosaicApi>(sp => sp.GetRequiredService<MosaicApiClient>());
        services.AddSingleton(sp => new AuthSessionManager(
            sp.GetRequiredService<MosaicApiClient>(),
            sp.GetRequiredService<TokenStore>()));
        services.AddSingleton<DeviceFlowClient>();
        services.AddSingleton<DeviceApprovalService>();

        services.AddSingleton<SnapshotBootstrapService>();

        // MosaicApp takes StartupService in its constructor, so ProjectionSession
        // is resolved before the first SynchronizationContext is installed.
        // Without an explicit UI context its Post runs inline on a worker thread
        // and ObservableCollection mutations from sync events cross threads.
        services.AddSingleton<MainThreadSynchronizationContext>();
        services.AddSingleton(sp => new ProjectionSession(
            sp.GetRequiredService<IMosaicApi>(),
            sp.GetRequiredService<SnapshotBootstrapService>(),
            FileSystem.AppDataDirectory,
            null,
            sp.GetRequiredService<MainThreadSynchronizationContext>()));

        services.AddSingleton<TaskListViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<SignInViewModel>();
        services.AddTransient<RegisterViewModel>();
        services.AddTransient<ForgotPasswordViewModel>();
        services.AddTransient<DeviceAuthorizeViewModel>();
        services.AddTransient<DeviceSignInViewModel>();

        // StartupService may first be resolved from MosaicApp's constructor,
        // before MAUI installs a SynchronizationContext. Use the same main-thread
        // adapter as ProjectionSession so auth events always reach the UI thread.
        services.AddSingleton(sp => new StartupService(
            sp.GetRequiredService<ProjectionSession>(),
            sp.GetRequiredService<AuthSessionManager>(),
            sp.GetRequiredService<TaskListViewModel>(),
            sp.GetRequiredService<AppPreferences>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<AppThemeService>(),
            sp.GetRequiredService<MainThreadSynchronizationContext>()));
        services.AddSingleton<AppShell>();
        services.AddSingleton<TasksPage>();
        services.AddSingleton<AccountPage>();
        services.AddSingleton<SettingsPage>();
        services.AddTransient<SignInPage>();
        services.AddTransient<RegisterPage>();
        services.AddTransient<ForgotPasswordPage>();
        services.AddTransient<DeviceAuthorizePage>();
        services.AddTransient<DeviceSignInPage>();
#if ANDROID
        ConfigureWidgetServices(services);
#endif
    }

#if ANDROID
    private static void ConfigureWidgetServices(IServiceCollection services)
    {
        // Widget broadcasts resolve the same singletons from the application
        // container. Registering them here keeps the widget on the shared token
        // store, projection and API client — no parallel HttpClient or cache.
        services.AddSingleton(global::Android.App.Application.Context);
        services.AddSingleton<TaskWriteClient>();
        services.AddSingleton<WidgetTaskReader>();
        services.AddSingleton<WidgetTaskRepository>();
        services.AddSingleton<WidgetRefresh>();
    }
#endif

#if ANDROID
    private static ISecretStorage CreateSecretStorage() =>
        new Platforms.Android.AndroidSecretStorage(global::Android.App.Application.Context);
#elif IOS
    private static ISecretStorage CreateSecretStorage() =>
        new Platforms.iOS.IosSecretStorage();
#else
    private static ISecretStorage CreateSecretStorage() =>
        new UnsupportedSecretStorage();
#endif
}

/// <summary>
/// Marshals projection events back to the UI thread even when the singleton was
/// first resolved during <see cref="MosaicApp"/> construction (before MAUI has
/// installed a synchronization context).
/// </summary>
internal sealed class MainThreadSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        MainThread.BeginInvokeOnMainThread(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (MainThread.IsMainThread)
        {
            d(state);
            return;
        }

        MainThread.InvokeOnMainThreadAsync(() => d(state)).GetAwaiter().GetResult();
    }
}

/// <summary>Only reached on targets without a secure store; sessions are unavailable.</summary>
internal sealed class UnsupportedSecretStorage : ISecretStorage
{
    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken) =>
        throw new SessionStoreUnavailableException("no platform secret storage");

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken) =>
        throw new SessionStoreUnavailableException("no platform secret storage");

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        throw new SessionStoreUnavailableException("no platform secret storage");
}
