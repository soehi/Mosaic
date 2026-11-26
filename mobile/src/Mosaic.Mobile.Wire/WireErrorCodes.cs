namespace Mosaic.Mobile.Wire;

/// <summary>
/// Machine codes. The canonical set is what Mosaic Server 0.1.1 emits; the
/// alias constants are older names that the client still accepts so an app
/// built against the pre-0.1.1 freeze keeps working. Behaviour keys off the
/// classification below, never off server prose.
/// </summary>
public static class WireErrorCodes
{
    // --- Canonical (server 0.1.1) ---
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthInvalidCode = "auth.invalid_code";
    public const string AuthCodeExpired = "auth.code_expired";
    public const string AuthCodeAttemptsExceeded = "auth.code_attempts_exceeded";
    public const string AuthWeakPassword = "auth.weak_password";
    public const string AuthRateLimited = "auth.rate_limited";
    public const string AuthRegistrationClosed = "auth.registration_closed";
    public const string AuthRequired = "auth.required";
    public const string AuthSessionRevoked = "auth.session_revoked";
    public const string AuthSessionExpired = "auth.session_expired";
    public const string AuthInvalidRefresh = "auth.invalid_refresh";
    public const string AuthRefreshReused = "auth.refresh_reused";
    public const string AuthReauthRequired = "auth.reauth_required";
    public const string DeviceInvalidCode = "device.invalid_code";
    public const string DeviceRateLimited = "device.rate_limited";
    public const string SyncInvalid = "sync.invalid";
    public const string SyncCursorInvalid = "sync.cursor_invalid";
    public const string SyncConflict = "sync.conflict";

    // --- Compatibility aliases ---
    public const string AuthCodeInvalidAlias = "auth.code_invalid";
    public const string AuthPasswordPolicyAlias = "auth.password_policy";
    public const string AuthInvalidEmailAlias = "auth.invalid_email";
    public const string AuthCodeLockedAlias = "auth.code_locked";
    public const string SyncIdempotencyMismatchAlias = "sync.idempotency_mismatch";

    // --- Generic server codes ---
    public const string RequestInvalid = "request.invalid";
    public const string ServerError = "server.error";
    public const string ServerUnavailable = "server.unavailable";
    public const string EmailNotConfigured = "email.not_configured";
    public const string EmailDeliveryFailed = "email.delivery_failed";

    // --- Snapshot bootstrap (server backup surface) ---
    public const string BackupNotFound = "backup.not_found";
    public const string BackupCorrupt = "backup.corrupt";

    /// <summary>Local code: the response body exceeded the caller's byte budget.</summary>
    public const string ClientResponseTooLarge = "client.response_too_large";

    /// <summary>Any code that means "the stored session can no longer be used".</summary>
    public static bool IsSessionGone(string? code) => code is
        AuthSessionRevoked or AuthSessionExpired or AuthRequired
        or AuthInvalidRefresh or AuthRefreshReused;
}
