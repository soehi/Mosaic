using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Sync.Contracts;

namespace Mosaic.Auth.Wpf.Internal;

internal static class AuthLocalization
{
    public static string Localize(string key, string fallback) =>
        MosaicLocalizationManager.TryFindResource(key) ?? fallback;

    // Prefers the localized resource named by the error's MessageKey, then a known code, and only
    // falls back to the service message (already localized by the core module).
    public static string Error(OperationError? error)
    {
        if (error is null) return Localize("Auth.Error.Unknown", "操作未完成，请稍后再试。");
        if (error.MessageKey is { } key && MosaicLocalizationManager.TryFindResource(key) is { } localized)
            return localized;
        var resourceKey = error.Code switch
        {
            AuthErrorCodes.ServerRequired => "Auth.Error.ServerRequired",
            AuthErrorCodes.ServerUntrusted => "Auth.Error.ServerUntrusted",
            AuthErrorCodes.NetworkUnavailable => "Auth.Error.Network",
            AuthErrorCodes.InvalidCredentials => "Auth.Error.InvalidCredentials",
            AuthErrorCodes.EmailTaken => "Auth.Error.EmailTaken",
            AuthErrorCodes.CodeInvalid => "Auth.Error.CodeInvalid",
            AuthErrorCodes.CodeExpired => "Auth.Error.CodeExpired",
            AuthErrorCodes.RateLimited => "Auth.Error.RateLimited",
            AuthErrorCodes.RegistrationClosed => "Auth.Error.RegistrationClosed",
            AuthErrorCodes.PasswordTooWeak => "Auth.Error.PasswordTooWeak",
            AuthErrorCodes.TokenExpired => "Auth.Error.TokenExpired",
            AuthErrorCodes.TokenInvalid => "Auth.Error.TokenInvalid",
            AuthErrorCodes.DeviceCodeInvalid => "Auth.Error.DeviceCodeInvalid",
            AuthErrorCodes.DeviceFlowNotStarted => "Auth.Error.DeviceFlowNotStarted",
            AuthErrorCodes.SecretsUnavailable => "Auth.Error.SecretsUnavailable",
            SyncErrorCodes.Disabled => "Auth.Error.SyncDisabled",
            SyncErrorCodes.NotSignedIn => "Auth.Error.NotSignedIn",
            SyncErrorCodes.NetworkUnavailable => "Auth.Error.Network",
            SyncErrorCodes.SnapshotInvalid => "Auth.Error.SnapshotInvalid",
            SyncErrorCodes.ServerUntrusted => "Auth.Error.ServerUntrusted",
            ErrorCodes.CapabilityUnavailable => "Auth.Error.Unavailable",
            ErrorCodes.Canceled => "Auth.Error.Canceled",
            ErrorCodes.ValidationFailed or ErrorCodes.InvalidRequest => "Auth.Error.Validation",
            _ => null
        };
        return resourceKey is not null && MosaicLocalizationManager.TryFindResource(resourceKey) is { } text
            ? text
            : error.Message;
    }
}

internal static class AuthLocalizationDictionaries
{
    public static System.Windows.ResourceDictionary Dictionary(bool english)
    {
        var uri = new Uri($"/Mosaic.Auth.Wpf;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative);
        return (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(uri);
    }
}
