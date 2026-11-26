using System.Text.Json;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth;

public enum AuthState
{
    Unknown = 0,
    SignedOut,
    SignedIn,
    SessionExpired,
}

/// <summary>
/// One auth transition. <paramref name="UserId"/> and <paramref name="Generation"/>
/// are captured under the same lock as the state, so a handler can tell which
/// account the event belongs to and drop an out-of-order delivery instead of
/// re-reading properties that a later transition may already have overwritten.
/// Both are optional so existing positional constructions keep compiling.
/// </summary>
public sealed record AuthStateChangedEventArgs(
    AuthState State,
    string? AccountEmail,
    bool CacheReadOnly,
    string? ReasonCode,
    string? UserId = null,
    long Generation = 0);

/// <summary>Outcome of confirming that the session belongs to the account it claims.</summary>
public enum IdentityVerificationStatus
{
    Verified = 0,

    /// <summary>The server refused the token or named a different account; the session is gone.</summary>
    Rejected,

    /// <summary>The check could not be completed (offline, 5xx, no id in the reply).</summary>
    Unavailable,
}

public sealed record IdentityVerificationResult(
    IdentityVerificationStatus Status,
    string? UserId,
    AuthError? Error)
{
    public static IdentityVerificationResult Verified(string userId) =>
        new(IdentityVerificationStatus.Verified, userId, null);

    public static IdentityVerificationResult Rejected(AuthError? error) =>
        new(IdentityVerificationStatus.Rejected, null, error);

    public static IdentityVerificationResult Unavailable(AuthError? error) =>
        new(IdentityVerificationStatus.Unavailable, null, error);
}

public sealed record SignOutResult(bool Completed, AuthError? Error)
{
    public static SignOutResult Done() => new(true, null);

    public static SignOutResult Failed(AuthError error) => new(false, error);
}

/// <summary>
/// Owns the session: sign-in/registration, the refresh-token lifecycle, and
/// the sign-out/revocation paths. The access token exists only in memory; the
/// refresh token is written through <see cref="TokenStore"/> and nowhere else.
///
/// Offline rule: a network failure never deletes the stored session. Only an
/// explicit server rejection (revoked/reused/expired) or a local sign-out does.
/// </summary>
public sealed class AuthSessionManager : IAccessTokenProvider, IDisposable
{
    private static readonly TimeSpan AccessTokenSkew = TimeSpan.FromSeconds(60);

    private readonly MosaicApiClient _api;
    private readonly TokenStore _tokenStore;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);

    private SessionDto? _session;
    private StoredSession? _stored;
    private string? _deviceId;
    private string _serverUrl = string.Empty;
    private AuthState _state = AuthState.Unknown;
    private bool _cacheReadOnly;
    private string? _storeDiagnostic;
    private Task<RefreshOutcome>? _refreshTask;
    private long _generation;
    private bool _identityVerified;

    public AuthSessionManager(MosaicApiClient api, TokenStore tokenStore, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(tokenStore);
        _api = api;
        _tokenStore = tokenStore;
        _time = time ?? TimeProvider.System;
        _api.TokenProvider = this;
    }

    public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when the app must delete its local (unencrypted) task/settings cache.</summary>
    public event EventHandler<string>? LocalDataCleared;

    public AuthState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public string ServerUrl => _serverUrl;

    public string? AccountEmail
    {
        get
        {
            lock (_gate)
            {
                return _session?.EffectiveUser?.Email ?? _stored?.AccountEmail;
            }
        }
    }

    public string? UserId
    {
        get
        {
            lock (_gate)
            {
                return _session?.EffectiveUser?.Id ?? _stored?.UserId;
            }
        }
    }

    /// <summary>
    /// The id confirmed by <c>GET v1/auth/me</c> for the current session. Null
    /// until that check succeeds; a sign-in, refresh, expiry or sign-out resets it.
    /// Projection files may only be keyed on a value returned here or, when the
    /// server is unreachable, on the encrypted stored session's id.
    /// </summary>
    public string? VerifiedUserId
    {
        get
        {
            lock (_gate)
            {
                return _identityVerified ? _session?.EffectiveUser?.Id ?? _stored?.UserId : null;
            }
        }
    }

    public string? DeviceId
    {
        get
        {
            lock (_gate)
            {
                return _deviceId;
            }
        }
    }

    /// <summary>True when a token exists but only the offline cache may be read.</summary>
    public bool CacheReadOnly
    {
        get
        {
            lock (_gate)
            {
                return _cacheReadOnly;
            }
        }
    }

    /// <summary>Non-secret diagnostic such as <c>secure_storage_unavailable</c>.</summary>
    public string? StoreDiagnostic
    {
        get
        {
            lock (_gate)
            {
                return _storeDiagnostic;
            }
        }
    }

    public string? AccessToken
    {
        get
        {
            lock (_gate)
            {
                return _session?.AccessToken;
            }
        }
    }

    public async Task<AuthResult> InitializeAsync(string? serverUrl, CancellationToken cancellationToken = default)
    {
        if (!ServerAddress.TryNormalize(serverUrl, out var normalized, out var error))
        {
            return AuthResult.Fail(new AuthError(AuthErrorKind.InvalidServer, error));
        }

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _api.BaseUrl = normalized;
            _serverUrl = normalized;
            ClearMemory();

            var load = await _tokenStore.LoadAsync(normalized, cancellationToken).ConfigureAwait(false);
            switch (load.Status)
            {
                case SessionStoreStatus.Loaded:
                    lock (_gate)
                    {
                        _stored = load.Session;
                        _deviceId = load.Session!.DeviceId;
                        _cacheReadOnly = false;
                    }

                    SetState(AuthState.SignedIn, "restored");
                    break;
                case SessionStoreStatus.Unavailable:
                    lock (_gate)
                    {
                        _storeDiagnostic = load.Diagnostic;
                    }

                    SetState(AuthState.SignedOut, load.Diagnostic);
                    break;
                default:
                    SetState(AuthState.SignedOut, null);
                    break;
            }

            return AuthResult.Ok();
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Validates a candidate server before the user commits to it.</summary>
    public Task<ApiResult<AuthPolicyDto>> ProbePolicyAsync(
        string? serverUrl,
        CancellationToken cancellationToken = default)
    {
        if (!ServerAddress.TryNormalize(serverUrl, out var normalized, out var error))
        {
            return Task.FromResult(
                ApiResult<AuthPolicyDto>.Fail(0, new WireErrorDto { Code = error ?? "server.url_invalid" }));
        }

        return _api.GetFromAsync<AuthPolicyDto>(normalized, "v1/auth/policy", cancellationToken);
    }

    /// <summary>Selects the server while signed out; a stored session blocks the switch.</summary>
    public AuthResult ConfigureServer(string? serverUrl)
    {
        if (!ServerAddress.TryNormalize(serverUrl, out var normalized, out var error))
        {
            return AuthResult.Fail(new AuthError(AuthErrorKind.InvalidServer, error));
        }

        lock (_gate)
        {
            if (_stored is not null || _state == AuthState.SignedIn)
            {
                return AuthResult.Fail(new AuthError(AuthErrorKind.InvalidServer, "server.switch_requires_sign_out"));
            }

            _api.BaseUrl = normalized;
            _serverUrl = normalized;
        }

        SetState(AuthState.SignedOut, null);
        return AuthResult.Ok();
    }

    public async Task<AuthResult<SessionDto>> SignInAsync(
        string email,
        string password,
        string? deviceLabel,
        CancellationToken cancellationToken = default)
    {
        if (!AuthInputRules.IsEmailShapeValid(email))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.EmailInvalid, "auth.invalid_email"));
        }

        if (string.IsNullOrEmpty(password))
        {
            return AuthResult<SessionDto>.Fail(
                new AuthError(AuthErrorKind.InvalidCredentials, "auth.invalid_credentials"));
        }

        var deviceId = await TryResolveDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        if (deviceId.Error is not null)
        {
            return AuthResult<SessionDto>.Fail(deviceId.Error);
        }

        var result = await _api.PostAsync<SessionDto>(
                "v1/auth/login",
                new LoginRequestDto
                {
                    Email = AuthInputRules.NormalizeEmail(email),
                    Password = password,
                    DeviceLabel = deviceLabel,
                },
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        return await FinishSignInAsync(result, deviceId.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResult<SessionDto>> RegisterAsync(
        string email,
        string password,
        string code,
        string? deviceLabel,
        CancellationToken cancellationToken = default)
    {
        if (!AuthInputRules.IsEmailShapeValid(email))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.EmailInvalid, "auth.invalid_email"));
        }

        if (!AuthInputRules.IsPasswordLengthValid(password))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.PasswordPolicy, "auth.weak_password"));
        }

        var deviceId = await TryResolveDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        if (deviceId.Error is not null)
        {
            return AuthResult<SessionDto>.Fail(deviceId.Error);
        }

        var result = await _api.PostAsync<SessionDto>(
                "v1/auth/register",
                new RegisterRequestDto
                {
                    Email = AuthInputRules.NormalizeEmail(email),
                    Password = password,
                    Code = code.Trim(),
                    DeviceLabel = deviceLabel,
                },
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        return await FinishSignInAsync(result, deviceId.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResult<SessionDto>> SignInWithCodeAsync(
        string email,
        string code,
        string? deviceLabel,
        CancellationToken cancellationToken = default)
    {
        if (!AuthInputRules.IsEmailShapeValid(email))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.EmailInvalid, "auth.invalid_email"));
        }

        if (!AuthInputRules.IsEmailCodeShapeValid(code))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.CodeInvalid, "auth.invalid_code"));
        }

        var deviceId = await TryResolveDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        if (deviceId.Error is not null)
        {
            return AuthResult<SessionDto>.Fail(deviceId.Error);
        }

        var result = await _api.PostAsync<SessionDto>(
                "v1/auth/login/code",
                new CodeLoginRequestDto
                {
                    Email = AuthInputRules.NormalizeEmail(email),
                    Code = code.Trim(),
                    DeviceLabel = deviceLabel,
                },
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        return await FinishSignInAsync(result, deviceId.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResult<EmailCodeAcceptedDto>> RequestEmailCodeAsync(
        string email,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        if (!AuthInputRules.IsEmailShapeValid(email))
        {
            return AuthResult<EmailCodeAcceptedDto>.Fail(
                new AuthError(AuthErrorKind.EmailInvalid, "auth.invalid_email"));
        }

        var result = await _api.PostAsync<EmailCodeAcceptedDto>(
                "v1/auth/email-code",
                new EmailCodeRequestDto(AuthInputRules.NormalizeEmail(email), purpose),
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? AuthResult<EmailCodeAcceptedDto>.Ok(result.Value ?? new EmailCodeAcceptedDto())
            : AuthResult<EmailCodeAcceptedDto>.Fail(result.StatusCode, result.Error, result.RetryAfter);
    }

    public async Task<AuthResult> ForgotPasswordAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        if (!AuthInputRules.IsEmailShapeValid(email))
        {
            return AuthResult.Fail(new AuthError(AuthErrorKind.EmailInvalid, "auth.invalid_email"));
        }

        var result = await _api.PostAsync(
                "v1/auth/forgot-password",
                new ForgotPasswordRequestDto(AuthInputRules.NormalizeEmail(email)),
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? AuthResult.Ok()
            : AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
    }

    public async Task<AuthResult> ResetPasswordAsync(
        string email,
        string code,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!AuthInputRules.IsPasswordLengthValid(newPassword))
        {
            return AuthResult.Fail(new AuthError(AuthErrorKind.PasswordPolicy, "auth.weak_password"));
        }

        var result = await _api.PostAsync(
                "v1/auth/reset-password",
                new ResetPasswordRequestDto
                {
                    Email = AuthInputRules.NormalizeEmail(email),
                    Code = code.Trim(),
                    NewPassword = newPassword,
                },
                authenticated: false,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        // Password reset revokes every server session. Keep the local cache as
        // a read-only projection; the user must sign in again.
        await ExpireAsync("password_reset", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
        return AuthResult.Ok();
    }

    public Task<AuthResult> RequestDeleteAccountCodeAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        ForgotPasswordAsyncCore(email, EmailCodePurpose.DeleteAccount, cancellationToken);

    private async Task<AuthResult> ForgotPasswordAsyncCore(
        string email,
        string purpose,
        CancellationToken cancellationToken)
    {
        var result = await RequestEmailCodeAsync(email, purpose, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? AuthResult.Ok() : AuthResult.Fail(result.Error!);
    }

    public async Task<AuthResult> DeleteAccountAsync(
        string? password,
        string? code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password) && string.IsNullOrWhiteSpace(code))
        {
            return AuthResult.Fail(new AuthError(AuthErrorKind.ReauthRequired, "auth.reauth_required"));
        }

        var result = await _api.PostAsync(
                "v1/auth/account/delete",
                new DeleteAccountRequestDto { Password = password, Code = code?.Trim() },
                authenticated: true,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        await ClearLocalSessionAsync("account_deleted", clearCache: true).ConfigureAwait(false);
        return AuthResult.Ok();
    }

    public async Task<AuthResult<IReadOnlyList<SessionInfoDto>>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _api.GetAsync<JsonElement>("v1/auth/sessions", authenticated: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AuthResult<IReadOnlyList<SessionInfoDto>>.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        return AuthResult<IReadOnlyList<SessionInfoDto>>.Ok(SessionListParser.Parse(result.Value));
    }

    public async Task<AuthResult> RevokeSessionAsync(
        string sessionId,
        bool isCurrentSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var result = await _api.PostAsync(
                $"v1/auth/sessions/{Uri.EscapeDataString(sessionId)}/revoke",
                body: null,
                authenticated: true,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        if (isCurrentSession)
        {
            await ExpireAsync("session_revoked_local", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
        }

        return AuthResult.Ok();
    }

    /// <param name="keepCurrent">
    /// When true the server keeps this device's session; the local session then
    /// stays usable. The default revokes every session, including this one.
    /// </param>
    public async Task<AuthResult> RevokeAllSessionsAsync(
        bool keepCurrent = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _api.PostAsync(
                "v1/auth/sessions/revoke-all",
                body: keepCurrent ? new RevokeAllSessionsRequestDto(true) : null,
                authenticated: true,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        if (!keepCurrent)
        {
            await ExpireAsync("sessions_revoked_all", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
        }

        return AuthResult.Ok();
    }

    /// <summary>
    /// Sign out. With <paramref name="localOnly"/> the refresh token is *not*
    /// revoked on the server; the UI must warn about that explicitly.
    /// </summary>
    public async Task<SignOutResult> SignOutAsync(
        bool allDevices,
        bool localOnly,
        CancellationToken cancellationToken = default)
    {
        StoredSession? stored;
        lock (_gate)
        {
            stored = _stored;
        }

        if (!localOnly && stored is not null)
        {
            var result = await _api.PostAsync(
                    "v1/auth/logout",
                    new LogoutRequestDto(stored.RefreshToken, allDevices),
                    authenticated: true,
                    retryTransient: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess && result.StatusCode is 0 or >= 500)
            {
                return SignOutResult.Failed(AuthError.FromApi(result.StatusCode, result.Error, result.RetryAfter));
            }
        }

        await ClearLocalSessionAsync("sign_out", clearCache: true).ConfigureAwait(false);
        return SignOutResult.Done();
    }

    /// <summary>Adopts the session returned by an approved device flow.</summary>
    public async Task<AuthResult<SessionDto>> CompleteDeviceSignInAsync(
        SessionDto session,
        CancellationToken cancellationToken = default)
    {
        session = session.Normalize(_time.GetUtcNow());
        var deviceId = await TryResolveDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        if (deviceId.Error is not null)
        {
            return AuthResult<SessionDto>.Fail(deviceId.Error);
        }

        var applied = await ApplySessionAsync(session, deviceId.Value!, cancellationToken).ConfigureAwait(false);
        if (!applied.Succeeded)
        {
            return applied;
        }

        // /auth/me is the only trusted identity source. A transport failure keeps
        // the session (tokens came over TLS from the poll) but records the check
        // as deferred; a 401 or an account mismatch invalidates it.
        var verified = await VerifyIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (verified.Status == IdentityVerificationStatus.Rejected)
        {
            return AuthResult<SessionDto>.Fail(verified.Error ?? AuthError.StorageUnavailable);
        }

        SetState(
            AuthState.SignedIn,
            verified.Status == IdentityVerificationStatus.Verified
                ? "device_verified"
                : "device_verification_deferred");

        return AuthResult<SessionDto>.Ok(session);
    }

    /// <summary>
    /// Confirms the current token belongs to the account it claims. This is the
    /// gate before a projection file is opened: 401 or a different id destroys the
    /// session, while an offline/5xx reply is non-fatal and lets the caller fall
    /// back to the encrypted stored id.
    /// </summary>
    public async Task<IdentityVerificationResult> VerifyIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        string? expected;
        lock (_gate)
        {
            expected = _session?.EffectiveUser?.Id ?? _stored?.UserId;
        }

        if (string.IsNullOrWhiteSpace(expected))
        {
            await ExpireAsync("identity_missing", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
            return IdentityVerificationResult.Rejected(
                new AuthError(AuthErrorKind.SessionRevoked, "auth.identity_missing"));
        }

        var me = await _api.GetAsync<MeResponseDto>(
                "v1/auth/me",
                authenticated: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!me.IsSuccess)
        {
            var error = AuthError.FromApi(me.StatusCode, me.Error, me.RetryAfter, isRefresh: true);
            if (me.StatusCode == 401 || error.Kind == AuthErrorKind.SessionRevoked)
            {
                await ExpireAsync("identity_rejected", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
                return IdentityVerificationResult.Rejected(error);
            }

            return IdentityVerificationResult.Unavailable(error);
        }

        var confirmed = me.Value?.EffectiveUser?.Id;
        if (string.IsNullOrWhiteSpace(confirmed))
        {
            return IdentityVerificationResult.Unavailable(
                new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
        }

        if (!string.Equals(confirmed, expected, StringComparison.Ordinal))
        {
            await ExpireAsync("identity_mismatch", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
            return IdentityVerificationResult.Rejected(
                new AuthError(AuthErrorKind.SessionRevoked, "auth.identity_mismatch"));
        }

        lock (_gate)
        {
            // The account might have changed while the request was in flight;
            // only mark the id verified for the session that was checked.
            if (string.Equals(_session?.EffectiveUser?.Id ?? _stored?.UserId, expected, StringComparison.Ordinal))
            {
                _identityVerified = true;
            }
        }

        return IdentityVerificationResult.Verified(confirmed);
    }

    // --- IAccessTokenProvider ---

    public async Task<string?> RefreshAsync(CancellationToken cancellationToken)
    {
        SessionDto? current;
        lock (_gate)
        {
            current = _session;
        }

        if (current?.AccessToken is { Length: > 0 } token
            && current.AccessTokenExpiresAt is { } expires
            && expires > _time.GetUtcNow().Add(AccessTokenSkew))
        {
            return token;
        }

        Task<RefreshOutcome> refresh;
        lock (_gate)
        {
            _refreshTask ??= RefreshCoreAsync();
            refresh = _refreshTask;
        }

        try
        {
            var outcome = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            return outcome.Token;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_refreshTask, refresh))
                {
                    _refreshTask = null;
                }
            }
        }
    }

    private async Task<RefreshOutcome> RefreshCoreAsync()
    {
        StoredSession? stored;
        lock (_gate)
        {
            stored = _stored;
        }

        if (stored is null || string.IsNullOrWhiteSpace(stored.RefreshToken))
        {
            return RefreshOutcome.Failed(new AuthError(AuthErrorKind.SessionRevoked, "auth.required"));
        }

        if (stored.RefreshTokenExpiresAt is { } expires && expires <= _time.GetUtcNow())
        {
            await ExpireAsync("refresh_expired", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
            return RefreshOutcome.Failed(new AuthError(AuthErrorKind.SessionRevoked, "auth.session_expired"));
        }

        var result = await _api.PostAsync<SessionDto>(
                "v1/auth/refresh",
                new RefreshRequestDto(stored.RefreshToken),
                authenticated: false,
                retryTransient: false,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var refreshed = result.Value?.Normalize(_time.GetUtcNow());
            if (!TryGetTokens(refreshed, out var accessToken, out var newRefresh))
            {
                return RefreshOutcome.Failed(new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
            }

            // Refresh is not allowed to move the session to a different account:
            // the refresh token belongs to `stored.UserId`, so a response naming
            // somebody else is a replay/mix-up and must not be persisted.
            var refreshedId = refreshed!.EffectiveUser?.Id;
            if (string.IsNullOrWhiteSpace(refreshedId))
            {
                return RefreshOutcome.Failed(new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
            }

            if (!string.IsNullOrWhiteSpace(stored.UserId)
                && !string.Equals(refreshedId, stored.UserId, StringComparison.Ordinal))
            {
                await ExpireAsync("identity_mismatch", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
                return RefreshOutcome.Failed(new AuthError(AuthErrorKind.SessionRevoked, "auth.identity_mismatch"));
            }

            var updated = stored with
            {
                RefreshToken = newRefresh!,
                RefreshTokenExpiresAt = refreshed!.RefreshTokenExpiresAt,
                AccountEmail = refreshed.EffectiveUser?.Email ?? stored.AccountEmail,
                UserId = refreshedId,
            };

            lock (_gate)
            {
                _session = refreshed;
                _stored = updated;
                _cacheReadOnly = false;
            }

            try
            {
                await _tokenStore.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SessionStoreUnavailableException)
            {
                // In-memory session stays usable for this run, but it cannot
                // survive a restart: surface it, never fall back to plaintext.
                lock (_gate)
                {
                    _storeDiagnostic = "refresh_persist_failed";
                }
            }

            SetState(AuthState.SignedIn, "refreshed");
            return RefreshOutcome.Success(accessToken!);
        }

        var error = AuthError.FromApi(result.StatusCode, result.Error, result.RetryAfter, isRefresh: true);
        if (error.Kind is AuthErrorKind.SessionRevoked or AuthErrorKind.RefreshReused or AuthErrorKind.InvalidCredentials)
        {
            await ExpireAsync("refresh_rejected", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
        }

        return RefreshOutcome.Failed(error);
    }

    private async Task<AuthResult<SessionDto>> FinishSignInAsync(
        ApiResult<SessionDto> result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            return AuthResult<SessionDto>.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        if (result.Value is null || !TryGetTokens(result.Value, out _, out _))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
        }

        return await ApplySessionAsync(result.Value, deviceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthResult<SessionDto>> ApplySessionAsync(
        SessionDto session,
        string deviceId,
        CancellationToken cancellationToken)
    {
        session = session.Normalize(_time.GetUtcNow());
        if (!TryGetTokens(session, out _, out var refreshToken))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
        }

        // Sign-in is the only path that legitimately installs a different
        // account. Detect that switch *before* overwriting the encrypted session
        // so the old account's on-device projection can be cleared by the
        // LocalDataCleared event instead of silently surviving under the new one.
        string? previousUserId;
        string? previousServer;
        lock (_gate)
        {
            previousUserId = _stored?.UserId;
            previousServer = _stored?.ServerUrl;
        }

        var incomingId = session.EffectiveUser?.Id;
        var switchingAccount = !string.IsNullOrWhiteSpace(previousUserId)
            && !string.IsNullOrWhiteSpace(incomingId)
            && string.Equals(previousServer, _serverUrl, StringComparison.Ordinal)
            && !string.Equals(previousUserId, incomingId, StringComparison.Ordinal);

        var stored = new StoredSession
        {
            ServerUrl = _serverUrl,
            DeviceId = deviceId,
            AccountEmail = session.EffectiveUser?.Email,
            UserId = incomingId,
            RefreshToken = refreshToken!,
            RefreshTokenExpiresAt = session.RefreshTokenExpiresAt,
            SavedAtUtc = _time.GetUtcNow(),
        };

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _tokenStore.SaveAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionStoreUnavailableException)
        {
            lock (_gate)
            {
                _session = null;
                _stored = null;
                _storeDiagnostic = "secure_storage_unavailable";
            }

            SetState(AuthState.SignedOut, "secure_storage_unavailable");
            return AuthResult<SessionDto>.Fail(AuthError.StorageUnavailable);
        }
        finally
        {
            _transition.Release();
        }

        lock (_gate)
        {
            _session = session;
            _stored = stored;
            _deviceId = deviceId;
            _cacheReadOnly = false;
            _storeDiagnostic = null;
            _identityVerified = false;
        }

        if (switchingAccount)
        {
            // Erase the outgoing account's projection before publishing the new
            // identity. Subscribers that read the current auth state on
            // StateChanged would otherwise clear the incoming account's store.
            LocalDataCleared?.Invoke(this, "identity_switched");
            SetState(AuthState.SignedIn, "identity_switched");
        }
        else
        {
            SetState(AuthState.SignedIn, "signed_in");
        }

        return AuthResult<SessionDto>.Ok(session);
    }

    private async Task<(string? Value, AuthError? Error)> TryResolveDeviceIdAsync(
        CancellationToken cancellationToken)
    {
        string? current;
        lock (_gate)
        {
            current = _deviceId;
        }

        if (current is { Length: > 0 })
        {
            return (current, null);
        }

        try
        {
            var deviceId = await _tokenStore.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _deviceId = deviceId;
            }

            return (deviceId, null);
        }
        catch (SessionStoreUnavailableException)
        {
            return (null, AuthError.StorageUnavailable);
        }
    }

    private async Task ExpireAsync(string reason, bool cacheReadOnly, bool clearTokens)
    {
        await _transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (clearTokens && !string.IsNullOrEmpty(_serverUrl))
            {
                try
                {
                    await _tokenStore.ClearAsync(_serverUrl, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SessionStoreUnavailableException)
                {
                    lock (_gate)
                    {
                        _storeDiagnostic = "secure_storage_delete_failed";
                    }
                }
            }

            lock (_gate)
            {
                _session = null;
                _stored = null;
                _identityVerified = false;
                if (cacheReadOnly)
                {
                    _cacheReadOnly = true;
                }
            }
        }
        finally
        {
            _transition.Release();
        }

        SetState(AuthState.SessionExpired, reason);
    }

    private async Task ClearLocalSessionAsync(string reason, bool clearCache)
    {
        await _transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_serverUrl))
            {
                try
                {
                    await _tokenStore.ClearAsync(_serverUrl, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SessionStoreUnavailableException)
                {
                    // Memory is cleared regardless; the record stays encrypted
                    // and undecryptable without the platform key.
                }
            }

            lock (_gate)
            {
                _session = null;
                _stored = null;
                _cacheReadOnly = false;
                _identityVerified = false;
            }
        }
        finally
        {
            _transition.Release();
        }

        if (clearCache)
        {
            // Publish the cache-clear first, while the outgoing account is still
            // identifiable. Subscribers reconcile on StateChanged and would
            // otherwise close the projection before it could be erased.
            LocalDataCleared?.Invoke(this, reason);
        }

        SetState(AuthState.SignedOut, reason);
    }

    private void ClearMemory()
    {
        lock (_gate)
        {
            _session = null;
            _stored = null;
            _deviceId = null;
            _cacheReadOnly = false;
            _storeDiagnostic = null;
            _refreshTask = null;
            _identityVerified = false;
        }
    }

    private void SetState(AuthState state, string? reason)
    {
        AuthStateChangedEventArgs args;
        lock (_gate)
        {
            _state = state;
            _generation++;

            // Snapshot identity and generation together with the state: a handler
            // that re-reads the properties may already see a later transition.
            args = new AuthStateChangedEventArgs(
                state,
                _session?.EffectiveUser?.Email ?? _stored?.AccountEmail,
                _cacheReadOnly,
                reason,
                _session?.EffectiveUser?.Id ?? _stored?.UserId,
                _generation);
        }

        StateChanged?.Invoke(this, args);
    }

    private static bool TryGetTokens(SessionDto? session, out string? accessToken, out string? refreshToken)
    {
        accessToken = session?.AccessToken;
        refreshToken = session?.RefreshToken;
        return !string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(refreshToken);
    }

    public void Dispose() => _transition.Dispose();

    private sealed record RefreshOutcome(string? Token, AuthError? Error)
    {
        public static RefreshOutcome Success(string token) => new(token, null);

        public static RefreshOutcome Failed(AuthError error) => new(null, error);
    }
}
