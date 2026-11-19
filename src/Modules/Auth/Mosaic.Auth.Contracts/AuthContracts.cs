using System.Globalization;
using Mosaic.Contracts;

namespace Mosaic.Auth.Contracts;

public enum AuthStatus
{
    SignedOut,
    SignedIn
}

public enum AuthEmailCodePurpose
{
    Register,
    Login,
    Reset,
    DeleteAccount
}

public enum DeviceAuthorizationStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed
}

public sealed record AuthAccountDto(Guid UserId, string Email, string? DisplayName);

public sealed record AuthPolicyDto(bool RegistrationOpen, bool RequireEmailVerification, string ServerName);

// Session secrets travel only between the transport, the core service and the platform secret store.
// They are never written to settings, configuration files, logs or diagnostics.
public sealed record AuthSessionDto(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    AuthAccountDto Account);

public sealed record DeviceAuthorizationDto(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    Uri? VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

public sealed record DevicePollDto(DeviceAuthorizationStatus Status, AuthSessionDto? Session, int? SlowDownIntervalSeconds = null);

public sealed record AuthSessionInfoDto(
    string Id,
    string? DeviceLabel,
    string? Platform,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Current);

public sealed record AuthState(AuthStatus Status, AuthAccountDto? Account, Uri? Server)
{
    public static AuthState SignedOut(Uri? server = null) => new(AuthStatus.SignedOut, null, server);
}

// Persisted credentials: refresh token plus the non-secret identity needed to show the account
// before the first network call after a restart.
public sealed record AuthSecrets(
    string RefreshToken,
    Guid DeviceId,
    Guid UserId,
    string Email,
    string? DisplayName,
    Uri Server);

public static class AuthErrorCodes
{
    public const string ServerRequired = "AUTH_SERVER_REQUIRED";
    public const string ServerUntrusted = "AUTH_SERVER_UNTRUSTED";
    public const string NetworkUnavailable = "AUTH_NETWORK_UNAVAILABLE";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string CodeInvalid = "CODE_INVALID";
    public const string CodeExpired = "CODE_EXPIRED";
    public const string RateLimited = "RATE_LIMITED";
    public const string RegistrationClosed = "REGISTRATION_CLOSED";
    public const string PasswordTooWeak = "PASSWORD_TOO_WEAK";
    public const string TokenExpired = "TOKEN_EXPIRED";
    public const string TokenInvalid = "TOKEN_INVALID";
    public const string DeviceCodeInvalid = "DEVICE_CODE_INVALID";
    public const string DeviceFlowNotStarted = "DEVICE_FLOW_NOT_STARTED";
    public const string SecretsUnavailable = "AUTH_SECRETS_UNAVAILABLE";
    public const string ReauthRequired = "AUTH_REAUTH_REQUIRED";
    public const string SessionRevoked = "AUTH_SESSION_REVOKED";
}

public static class AuthValidation
{
    // The instance this build ships with. The account page pre-fills the server box with it so a
    // fresh install connects without typing; it is only a default, never an automatic connection,
    // and the user may replace it with any other HTTPS self-hosted server.
    public const string DefaultServerUrl = "https://mosaic.bansonic.top";
    public const int PasswordMinLength = 10;
    public const int PasswordMaxLength = 256;
    public const int EmailCodeLength = 6;

    public static string NormalizeEmail(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidEmail(string? email)
    {
        var normalized = NormalizeEmail(email);
        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == normalized.Length - 1 || normalized.Contains(' ')) return false;
        if (normalized.IndexOf('@', at + 1) >= 0) return false;
        var domain = normalized[(at + 1)..];
        var dot = domain.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot < domain.Length - 1 && normalized.Length <= 254;
    }

    public static bool IsValidPassword(string? password) =>
        password is { Length: >= PasswordMinLength and <= PasswordMaxLength };

    public static bool IsValidEmailCode(string? code) =>
        code is { Length: EmailCodeLength } && code.All(char.IsAsciiDigit);

    // Only absolute HTTPS origins are accepted. Plain HTTP is never allowed, no matter the host.
    public static bool IsTrustedServer(Uri? server) =>
        server is { IsAbsoluteUri: true } &&
        string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(server.UserInfo) &&
        string.IsNullOrEmpty(server.Query) &&
        string.IsNullOrEmpty(server.Fragment) &&
        server.AbsolutePath is "/" or "";

    public static bool TryParseServer(string? value, out Uri? server)
    {
        server = null;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = Uri.UriSchemeHttps + "://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var candidate)) return false;
        var normalized = new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        if (!IsTrustedServer(normalized)) return false;
        server = normalized;
        return true;
    }

    public static string FormatServer(Uri server) =>
        server.GetLeftPart(UriPartial.Authority).ToLower(CultureInfo.InvariantCulture);
}

public interface IAuthSecretStore
{
    Task<OperationResult<AuthSecrets?>> LoadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(AuthSecrets secrets, CancellationToken cancellationToken = default);
    Task<OperationResult> ClearAsync(CancellationToken cancellationToken = default);
}

public interface IAuthTransport
{
    Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(Uri server, CancellationToken cancellationToken = default);
    Task<OperationResult> SendEmailCodeAsync(Uri server, string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default);
    Task<OperationResult<AuthSessionDto>> RegisterAsync(Uri server, string email, string password, string code, CancellationToken cancellationToken = default);
    Task<OperationResult<AuthSessionDto>> LoginAsync(Uri server, string email, string password, CancellationToken cancellationToken = default);
    Task<OperationResult<AuthSessionDto>> LoginWithCodeAsync(Uri server, string email, string code, CancellationToken cancellationToken = default);
    Task<OperationResult<AuthSessionDto>> RefreshAsync(Uri server, string refreshToken, CancellationToken cancellationToken = default);
    Task<OperationResult> LogoutAsync(Uri server, string accessToken, string refreshToken, CancellationToken cancellationToken = default);
    Task<OperationResult> ForgotPasswordAsync(Uri server, string email, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetPasswordAsync(Uri server, string email, string code, string password, CancellationToken cancellationToken = default);
    Task<OperationResult<AuthAccountDto>> GetAccountAsync(Uri server, string accessToken, CancellationToken cancellationToken = default);
    Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(Uri server, string appVersion, CancellationToken cancellationToken = default);
    Task<OperationResult<DevicePollDto>> PollDeviceAuthorizationAsync(Uri server, string deviceCode, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<AuthSessionInfoDto>>> ListSessionsAsync(Uri server, string accessToken, CancellationToken cancellationToken = default);
    Task<OperationResult> RevokeSessionAsync(Uri server, string accessToken, string sessionId, CancellationToken cancellationToken = default);
    Task<OperationResult> RevokeAllSessionsAsync(Uri server, string accessToken, bool keepCurrent, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAccountAsync(Uri server, string accessToken, string? password, string? code, CancellationToken cancellationToken = default);
}

public interface IAuthApi
{
    AuthState State { get; }
    Guid DeviceId { get; }
    event EventHandler? StateChanged;

    Task<OperationResult<AuthPolicyDto>> GetPolicyAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SetServerAsync(string serverUrl, CancellationToken cancellationToken = default);

    Task<OperationResult> RequestEmailCodeAsync(string email, AuthEmailCodePurpose purpose, CancellationToken cancellationToken = default);
    Task<OperationResult> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default);
    Task<OperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<OperationResult> LoginWithCodeAsync(string email, string code, CancellationToken cancellationToken = default);
    Task<OperationResult> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default);

    Task<OperationResult<DeviceAuthorizationDto>> StartDeviceAuthorizationAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<DevicePollDto>> PollDeviceAuthorizationAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CancelDeviceAuthorizationAsync();

    Task<OperationResult> LogoutAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<IReadOnlyList<AuthSessionInfoDto>>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<OperationResult> RevokeAllSessionsAsync(bool keepCurrent, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAccountAsync(string? password, string? code, CancellationToken cancellationToken = default);
}
