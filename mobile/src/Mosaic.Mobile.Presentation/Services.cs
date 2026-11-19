using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

/// <summary>Resolved light/dark state. High contrast is a desktop-only choice.</summary>
public sealed class AppThemeService
{
    public ThemePreference Preference { get; private set; } = ThemePreference.FollowSystem;

    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    public void Apply(ThemePreference preference, bool systemIsDark)
    {
        Preference = preference;
        var dark = ResolveIsDark(preference, systemIsDark);
        if (dark == IsDark && preference == Preference)
        {
            return;
        }

        IsDark = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool ResolveIsDark(ThemePreference preference, bool systemIsDark) => preference switch
    {
        ThemePreference.Light => false,
        ThemePreference.Dark => true,
        _ => systemIsDark,
    };
}

public static class AuthErrorText
{
    public static string Describe(LocalizationService localizer, AuthError? error)
    {
        if (error is null)
        {
            return string.Empty;
        }

        if (error.Code == "server.url_https_required")
        {
            return localizer.T(StringKeys.ServerHttpsRequired);
        }

        if (error.Code == "server.switch_requires_sign_out")
        {
            return localizer.T(StringKeys.ServerSwitchRequiresSignOut);
        }

        if (error.Code is "server.url_invalid" or "server.url_required" or "server.url_scheme")
        {
            return localizer.T(StringKeys.ServerInvalid);
        }

        return error.Kind switch
        {
            AuthErrorKind.InvalidCredentials => localizer.T(StringKeys.InvalidCredentials),
            AuthErrorKind.CodeInvalid => localizer.T(StringKeys.CodeInvalid),
            AuthErrorKind.CodeExpired => localizer.T(StringKeys.CodeExpired),
            AuthErrorKind.CodeAttemptsExceeded => localizer.T(StringKeys.CodeAttemptsExceeded),
            AuthErrorKind.RateLimited => localizer.T(StringKeys.RateLimited),
            AuthErrorKind.SessionRevoked or AuthErrorKind.RefreshReused => localizer.T(StringKeys.SessionExpired),
            AuthErrorKind.PasswordPolicy => localizer.T(StringKeys.PasswordPolicy),
            AuthErrorKind.EmailInvalid => localizer.T(StringKeys.EmailInvalid),
            AuthErrorKind.ReauthRequired => localizer.T(StringKeys.ReauthRequired),
            AuthErrorKind.RegistrationClosed => localizer.T(StringKeys.RegistrationClosed),
            AuthErrorKind.EmailDeliveryFailed => localizer.T(StringKeys.EmailDeliveryFailed),
            AuthErrorKind.DeviceCodeInvalid => localizer.T(StringKeys.DeviceCodeInvalid),
            AuthErrorKind.DeviceAlreadyHandled => localizer.T(StringKeys.DeviceAlreadyHandled),
            AuthErrorKind.Network => localizer.T(StringKeys.NetworkError),
            AuthErrorKind.ServerUnavailable => localizer.T(StringKeys.ServerUnavailableText),
            AuthErrorKind.Protocol => localizer.T(StringKeys.ProtocolError),
            AuthErrorKind.StorageUnavailable => localizer.T(StringKeys.StorageUnavailable),
            AuthErrorKind.InvalidServer => localizer.T(StringKeys.ServerInvalid),
            AuthErrorKind.ServerNotFound => localizer.T(StringKeys.ServerInvalid),
            AuthErrorKind.Cancelled => string.Empty,
            _ => localizer.T(StringKeys.UnknownError),
        };
    }
}

public static class SyncStatusText
{
    public static string Describe(LocalizationService localizer, SyncStatusSnapshot status) =>
        status.Status switch
        {
            SyncStatusType.Syncing => localizer.T(StringKeys.Syncing),
            SyncStatusType.Succeeded => localizer.T(StringKeys.SyncSucceeded),
            SyncStatusType.Offline => localizer.T(StringKeys.SyncOffline),
            SyncStatusType.NeedsSignIn => localizer.T(StringKeys.SyncNeedsSignIn),
            SyncStatusType.RebuildRequired => localizer.T(StringKeys.SyncRebuildRequired),
            SyncStatusType.Failed => localizer.T(StringKeys.SyncFailed),
            _ => localizer.T(StringKeys.SyncIdle),
        };

    public static string LastSynced(LocalizationService localizer, DateTimeOffset? value) =>
        value is null
            ? localizer.T(StringKeys.LastSyncedNever)
            : localizer.Format(
                StringKeys.LastSyncedAt,
                value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
}

public abstract class ViewModelBase : ObservableObject
{
    protected ViewModelBase(LocalizationService localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Localizer = localizer;
        localizer.LanguageChanged += OnLanguageChanged;
    }

    protected LocalizationService Localizer { get; }

    /// <summary>Bindable indexer for static labels: <c>{Binding Strings[app.name]}</c>.</summary>
    public LocalizationService Strings => Localizer;

    protected string T(string key) => Localizer.T(key);

    protected string F(string key, params object?[] arguments) => Localizer.Format(key, arguments);

    protected string Describe(AuthError? error) => AuthErrorText.Describe(Localizer, error);

    protected virtual void RefreshLocalized()
    {
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalized();
        OnPropertyChanged(string.Empty);
    }

    public virtual void Dispose()
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }
}
