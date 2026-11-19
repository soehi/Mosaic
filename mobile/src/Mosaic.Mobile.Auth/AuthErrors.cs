using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth;

public enum AuthErrorKind
{
    None = 0,
    InvalidCredentials,
    CodeInvalid,
    CodeExpired,
    CodeAttemptsExceeded,
    RateLimited,
    SessionRevoked,
    RefreshReused,
    PasswordPolicy,
    EmailInvalid,
    ReauthRequired,
    RegistrationClosed,
    EmailUnavailable,
    EmailDeliveryFailed,
    DeviceCodeInvalid,
    DeviceAlreadyHandled,
    Network,
    ServerUnavailable,
    Protocol,
    Cancelled,
    StorageUnavailable,
    InvalidServer,
    ServerNotFound,
    Unknown,
}

/// <summary>
/// Display-ready failure. <see cref="Code"/> keeps the machine code for
/// diagnostics; <see cref="Message"/> is a localized fallback chosen by the
/// caller, never the raw server prose.
/// </summary>
public sealed record AuthError(
    AuthErrorKind Kind,
    string? Code = null,
    string? Message = null,
    TimeSpan? RetryAfter = null)
{
    public static AuthError Cancelled { get; } = new(AuthErrorKind.Cancelled);

    public static AuthError StorageUnavailable { get; } =
        new(AuthErrorKind.StorageUnavailable, "client.storage_unavailable");

    public static AuthError FromApi(int statusCode, WireErrorDto? error, TimeSpan? retryAfter, bool isRefresh = false)
    {
        var code = error?.Code;
        var kind = Classify(statusCode, code, isRefresh);
        return new AuthError(kind, code, error?.Message, retryAfter);
    }

    public static AuthErrorKind Classify(int statusCode, string? code, bool isRefresh = false)
    {
        switch (code)
        {
            case WireErrorCodes.AuthInvalidCredentials:
                return isRefresh ? AuthErrorKind.SessionRevoked : AuthErrorKind.InvalidCredentials;
            case WireErrorCodes.AuthInvalidCode:
            case WireErrorCodes.AuthCodeInvalidAlias:
                return AuthErrorKind.CodeInvalid;
            case WireErrorCodes.AuthCodeExpired:
                return AuthErrorKind.CodeExpired;
            case WireErrorCodes.AuthCodeAttemptsExceeded:
            case WireErrorCodes.AuthCodeLockedAlias:
                return AuthErrorKind.CodeAttemptsExceeded;
            case WireErrorCodes.AuthRateLimited:
            case WireErrorCodes.DeviceRateLimited:
                return AuthErrorKind.RateLimited;
            case WireErrorCodes.AuthSessionRevoked:
            case WireErrorCodes.AuthSessionExpired:
            case WireErrorCodes.AuthRequired:
            case WireErrorCodes.AuthInvalidRefresh:
                return AuthErrorKind.SessionRevoked;
            case WireErrorCodes.AuthRefreshReused:
                return AuthErrorKind.RefreshReused;
            case WireErrorCodes.AuthWeakPassword:
            case WireErrorCodes.AuthPasswordPolicyAlias:
                return AuthErrorKind.PasswordPolicy;
            case WireErrorCodes.AuthInvalidEmailAlias:
                return AuthErrorKind.EmailInvalid;
            case WireErrorCodes.AuthReauthRequired:
                return AuthErrorKind.ReauthRequired;
            case WireErrorCodes.AuthRegistrationClosed:
                return AuthErrorKind.RegistrationClosed;
            case WireErrorCodes.DeviceInvalidCode:
                return AuthErrorKind.DeviceCodeInvalid;
            case LocalTransportCodes.Network:
            case LocalTransportCodes.Timeout:
                return AuthErrorKind.Network;
            case LocalTransportCodes.Protocol:
                return AuthErrorKind.Protocol;
            case WireErrorCodes.ServerUnavailable:
                return AuthErrorKind.ServerUnavailable;
            case WireErrorCodes.EmailNotConfigured:
            case WireErrorCodes.EmailDeliveryFailed:
                return AuthErrorKind.EmailDeliveryFailed;
        }

        if (statusCode == 429)
        {
            return AuthErrorKind.RateLimited;
        }

        if (statusCode >= 500 || statusCode == 0)
        {
            return AuthErrorKind.ServerUnavailable;
        }

        if (statusCode == 401)
        {
            return isRefresh ? AuthErrorKind.SessionRevoked : AuthErrorKind.InvalidCredentials;
        }

        if (statusCode == 403)
        {
            return AuthErrorKind.ReauthRequired;
        }

        if (statusCode == 422)
        {
            return AuthErrorKind.PasswordPolicy;
        }

        return AuthErrorKind.Unknown;
    }
}

/// <summary>Result of an auth operation; <see cref="Succeeded"/> covers no-value commands too.</summary>
public sealed record AuthResult<T>(T? Value, AuthError? Error)
{
    public bool Succeeded => Error is null;

    public TimeSpan? RetryAfter => Error?.RetryAfter;

    public static AuthResult<T> Ok(T value) => new(value, null);

    public static AuthResult<T> Fail(AuthError error) => new(default, error);

    public static AuthResult<T> Fail(int statusCode, WireErrorDto? error, TimeSpan? retryAfter = null) =>
        new(default, AuthError.FromApi(statusCode, error, retryAfter));
}

public sealed record AuthResult(AuthError? Error)
{
    public bool Succeeded => Error is null;

    public TimeSpan? RetryAfter => Error?.RetryAfter;

    public static AuthResult Ok() => new(Error: null);

    public static AuthResult Fail(AuthError error) => new(error);

    public static AuthResult Fail(int statusCode, WireErrorDto? error, TimeSpan? retryAfter = null) =>
        new(AuthError.FromApi(statusCode, error, retryAfter));
}
