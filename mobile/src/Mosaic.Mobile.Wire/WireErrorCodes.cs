namespace Mosaic.Mobile.Wire;

/// <summary>
/// Machine codes. Frozen codes come from the shared protocol document §3.4.
/// Aliases are the codes the current server build emits; they map to the same
/// client behaviour so an app built against the frozen contract still works
/// against today's server. See docs/mobile-implementation-status.md for the
/// reported drift between the two lists.
/// </summary>
public static class WireErrorCodes
{
    // --- Frozen codes (shared protocol §3.4) ---
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthCodeInvalid = "auth.code_invalid";
    public const string AuthCodeExpired = "auth.code_expired";
    public const string AuthCodeAttemptsExceeded = "auth.code_attempts_exceeded";
    public const string AuthRateLimited = "auth.rate_limited";
    public const string AuthSessionRevoked = "auth.session_revoked";
    public const string AuthRefreshReused = "auth.refresh_reused";
    public const string AuthPasswordPolicy = "auth.password_policy";
    public const string AuthEmailInvalid = "auth.email_invalid";
    public const string AuthReauthRequired = "auth.reauth_required";
    public const string SyncCursorInvalid = "sync.cursor_invalid";
    public const string SyncConflict = "sync.conflict";
    public const string SyncIdempotencyMismatch = "sync.idempotency_mismatch";
    public const string ServerUnavailable = "server.unavailable";

    // --- Aliases observed in the current server implementation ---
    public const string AuthInvalidCodeAlias = "auth.invalid_code";
    public const string AuthCodeLockedAlias = "auth.code_locked";
    public const string AuthWeakPasswordAlias = "auth.weak_password";
    public const string AuthInvalidEmailAlias = "auth.invalid_email";
    public const string AuthSessionExpiredAlias = "auth.session_expired";
    public const string AuthRequiredAlias = "auth.required";
    public const string AuthInvalidRefreshAlias = "auth.invalid_refresh";
    public const string AuthRegistrationClosed = "auth.registration_closed";
    public const string DeviceInvalidCode = "device.invalid_code";
    public const string DeviceAlreadyHandled = "device.already_handled";
    public const string DeviceRateLimited = "device.rate_limited";
    public const string RequestInvalid = "request.invalid";
    public const string ServerError = "server.error";

    /// <summary>Any code that means "the stored session can no longer be used".</summary>
    public static bool IsSessionGone(string? code) => code is
        AuthSessionRevoked or AuthSessionExpiredAlias or AuthRequiredAlias
        or AuthRefreshReused or AuthInvalidRefreshAlias;
}
