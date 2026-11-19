namespace Mosaic.Mobile.Auth;

/// <summary>
/// Platform-protected byte storage (Android Keystore / iOS Keychain). The
/// implementation encrypts at rest; callers store only JSON they built from
/// <see cref="StoredSession"/> or the device identity record.
///
/// A failure here is a session-unavailable condition, never a crash: the app
/// keeps working offline and asks the user to sign in again.
/// </summary>
public interface ISecretStorage
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);

    Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// The only persisted credential record. The access token is deliberately
/// absent: it lives in memory and is re-fetched via the refresh token.
/// </summary>
public sealed record StoredSession
{
    public string ServerUrl { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string? AccountEmail { get; init; }

    public string? UserId { get; init; }

    public string RefreshToken { get; init; } = string.Empty;

    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

    public DateTimeOffset SavedAtUtc { get; init; }
}

public enum SessionStoreStatus
{
    Missing,
    Loaded,
    Unavailable,
}

public sealed record SessionLoadResult(
    SessionStoreStatus Status,
    StoredSession? Session,
    string? Diagnostic = null)
{
    public static SessionLoadResult Missing { get; } = new(SessionStoreStatus.Missing, null);

    public static SessionLoadResult Loaded(StoredSession session) =>
        new(SessionStoreStatus.Loaded, session);

    public static SessionLoadResult Unavailable(string diagnostic) =>
        new(SessionStoreStatus.Unavailable, null, diagnostic);
}

/// <summary>Thrown by the store when the platform secure storage cannot be used.</summary>
public sealed class SessionStoreUnavailableException : Exception
{
    public SessionStoreUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
