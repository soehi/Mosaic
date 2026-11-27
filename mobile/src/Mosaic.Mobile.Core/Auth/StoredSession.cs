namespace Mosaic.Mobile.Core.Auth;

/// <summary>
/// The persisted credential record. It is written only through
/// <see cref="ISecretStorage"/> and never appears in logs, plain files or
/// platform backup (the Android host disables backup entirely).
/// </summary>
public sealed record StoredSession
{
    public string ServerOrigin { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    public string RefreshToken { get; init; } = string.Empty;

    public DateTimeOffset AccessTokenExpiresAt { get; init; }

    public DateTimeOffset RefreshTokenExpiresAt { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    public string? DeviceLabel { get; init; }
}

public enum SessionState
{
    /// <summary>The persisted record exists and its identity was verified.</summary>
    SignedIn = 0,

    /// <summary>No record for this server; the card asks for provisioning.</summary>
    SignedOut,

    /// <summary>The refresh token is gone or was rejected; tokens were cleared.</summary>
    Expired,

    /// <summary>The credential store could not be read or written.</summary>
    Unavailable,
}

/// <summary>
/// Restore outcome. <paramref name="Offline"/> marks a verified session whose
/// network check could not run: cached projection data stays displayable.
/// </summary>
public sealed record SessionRestoreResult(SessionState State, string? UserId, bool Offline, string? ErrorCode)
{
    public static SessionRestoreResult SignedIn(string userId, bool offline = false) => new(SessionState.SignedIn, userId, offline, null);

    public static SessionRestoreResult SignedOut() => new(SessionState.SignedOut, null, false, null);

    public static SessionRestoreResult Expired(string? code = null) => new(SessionState.Expired, null, false, code);

    public static SessionRestoreResult Unavailable(string? code = null) => new(SessionState.Unavailable, null, false, code);
}

public static class SecretStorageKeys
{
    public const string SessionPrefix = "mosaic.mobile.v3.session.";

    public const string DeviceId = "mosaic.mobile.v3.device";

    public const string PendingDeviceFlow = "mosaic.mobile.v3.deviceflow.";
}