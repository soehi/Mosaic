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

public sealed record AuthStateChangedEventArgs(
    AuthState State,
    string? AccountEmail,
    bool CacheReadOnly,
    string? ReasonCode);

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
                return _session?.User?.Email ?? _stored?.AccountEmail;
            }
        }
    }

    public string? UserId
    {
        get
        {
            lock (_gate)
            {
                return _session?.User?.Id ?? _stored?.UserId;
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

    public async Task<AuthResult> RevokeAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _api.PostAsync(
                "v1/auth/sessions/revoke-all",
                body: null,
                authenticated: true,
                retryTransient: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return AuthResult.Fail(result.StatusCode, result.Error, result.RetryAfter);
        }

        await ExpireAsync("sessions_revoked_all", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
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

        // Verify the account identity. A transport failure keeps the session
        // (tokens came over TLS from the poll) but records the deferred check.
        var me = await _api.GetAsync<MeResponseDto>("v1/auth/me", authenticated: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (me.IsSuccess && me.Value?.User is { } user)
        {
            lock (_gate)
            {
                if (_session is not null)
                {
                    _session = _session with { User = user };
                }
            }

            SetState(AuthState.SignedIn, "device_verified");
        }
        else if (me.StatusCode == 401)
        {
            await ExpireAsync("device_verify_rejected", cacheReadOnly: true, clearTokens: true).ConfigureAwait(false);
            return AuthResult<SessionDto>.Fail(
                AuthError.FromApi(me.StatusCode, me.Error, me.RetryAfter, isRefresh: true));
        }
        else
        {
            SetState(AuthState.SignedIn, "device_verification_deferred");
        }

        return AuthResult<SessionDto>.Ok(session);
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
            if (!TryGetTokens(result.Value, out var accessToken, out var newRefresh))
            {
                return RefreshOutcome.Failed(new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
            }

            var updated = stored with
            {
                RefreshToken = newRefresh!,
                RefreshTokenExpiresAt = result.Value!.RefreshTokenExpiresAt,
                AccountEmail = result.Value.User?.Email ?? stored.AccountEmail,
                UserId = result.Value.User?.Id ?? stored.UserId,
            };

            lock (_gate)
            {
                _session = result.Value;
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
        if (!TryGetTokens(session, out _, out var refreshToken))
        {
            return AuthResult<SessionDto>.Fail(new AuthError(AuthErrorKind.Protocol, LocalTransportCodes.Protocol));
        }

        var stored = new StoredSession
        {
            ServerUrl = _serverUrl,
            DeviceId = deviceId,
            AccountEmail = session.User?.Email,
            UserId = session.User?.Id,
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
        }

        SetState(AuthState.SignedIn, "signed_in");
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
            }
        }
        finally
        {
            _transition.Release();
        }

        SetState(AuthState.SignedOut, reason);
        if (clearCache)
        {
            LocalDataCleared?.Invoke(this, reason);
        }
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
        }
    }

    private void SetState(AuthState state, string? reason)
    {
        lock (_gate)
        {
            _state = state;
        }

        StateChanged?.Invoke(
            this,
            new AuthStateChangedEventArgs(state, AccountEmail, CacheReadOnly, reason));
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
