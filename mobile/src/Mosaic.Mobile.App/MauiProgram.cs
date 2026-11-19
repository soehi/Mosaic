using Microsoft.Extensions.Logging;
using Mosaic.Mobile.App.Pages;
using Mosaic.Mobile.App.Services;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

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
        var version = "0.1.0";
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
        services.AddSingleton(sp => new MobileDatabase(
            Path.Combine(FileSystem.AppDataDirectory, "mosaic-mobile.db")));
        services.AddSingleton<SqliteSyncStore>();
        services.AddSingleton<ISyncStore>(sp => sp.GetRequiredService<SqliteSyncStore>());
        services.AddSingleton<ISecretStorage>(_ => CreateSecretStorage());
        services.AddSingleton(sp => new TokenStore(sp.GetRequiredService<ISecretStorage>()));

        var clientInfo = new MobileClientInfo(
            MobileDeviceInfo.AppVersion,
            MobileDeviceInfo.Platform,
            DeviceInfo.VersionString);
        services.AddSingleton(_ => new MosaicApiClient(MosaicApiClient.CreateHttpClient(clientInfo)));
        services.AddSingleton(sp => new AuthSessionManager(
            sp.GetRequiredService<MosaicApiClient>(),
            sp.GetRequiredService<TokenStore>()));
        services.AddSingleton<DeviceFlowClient>();
        services.AddSingleton<DeviceApprovalService>();

        services.AddSingleton<PortableSettings>();
        services.AddSingleton<BackupRestoreService>();
        services.AddSingleton<SyncEngine>();

        services.AddSingleton<TaskListViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<SignInViewModel>();
        services.AddTransient<RegisterViewModel>();
        services.AddTransient<ForgotPasswordViewModel>();
        services.AddTransient<DeviceAuthorizeViewModel>();
        services.AddTransient<DeviceSignInViewModel>();

        services.AddSingleton<StartupService>();
        services.AddSingleton<AppShell>();
        services.AddSingleton<TasksPage>();
        services.AddSingleton<AccountPage>();
        services.AddSingleton<SettingsPage>();
        services.AddTransient<SignInPage>();
        services.AddTransient<RegisterPage>();
        services.AddTransient<ForgotPasswordPage>();
        services.AddTransient<DeviceAuthorizePage>();
        services.AddTransient<DeviceSignInPage>();
    }

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
