using Mosaic.Mobile.Core.Api;
using Mosaic.Mobile.Core.Configuration;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Auth;

/// <summary>
/// Headless session restore. Semantically equivalent to the old
/// <c>AuthSessionManager.InitializeAsync(serverUrl, ct)</c> but with no UI
/// dependency: load the stored record, refresh when the access token is near
/// expiry, then verify the account identity against <c>/v1/auth/me</c> before
/// any projection is opened.
///
/// A 401 at any point clears the stored tokens and reports
/// <see cref="SessionState.Expired"/>; a transport failure reports an offline
/// but still signed-in session so cached data stays displayable.
/// </summary>
public sealed class SessionManager : IAccessTokenSource
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly IMosaicApiFactory _apiFactory;
    private readonly TokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ServerConfiguration? _configuration;
    private IMosaicApi? _api;
    private StoredSession? _session;

    public SessionManager(IMosaicApiFactory apiFactory, TokenStore tokens, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(apiFactory);
        ArgumentNullException.ThrowIfNull(tokens);
        _apiFactory = apiFactory;
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
    }

    public string? UserId => _session?.UserId;

    public Uri? BaseUri => _configuration?.BaseUri;

    public async Task<SessionRestoreResult> RestoreAsync(ServerConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _configuration = configuration;
            _api = _apiFactory.Create(configuration.BaseUri);

            StoredSession? stored;
            try
            {
                stored = await _tokens.LoadSessionAsync(configuration.NormalizedOrigin, cancellationToken).ConfigureAwait(false);
            }
            catch (SecretStorageException)
            {
                _session = null;
                return SessionRestoreResult.Unavailable("store.unavailable");
            }

            if (stored is null)
            {
                _session = null;
                return SessionRestoreResult.SignedOut();
            }

            _session = stored;
            if (stored.RefreshTokenExpiresAt <= _time.GetUtcNow())
            {
                await ClearAsync(cancellationToken).ConfigureAwait(false);
                return SessionRestoreResult.Expired("auth.refresh_expired");
            }

            var refreshedOffline = false;
            if (NeedsRefresh(stored))
            {
                var refresh = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                switch (refresh)
                {
                    case RefreshOutcome.SessionExpired:
                        return SessionRestoreResult.Expired("auth.session_expired");
                    case RefreshOutcome.Offline:
                        refreshedOffline = true;
                        break;
                    case RefreshOutcome.Succeeded:
                        break;
                }
            }

            var verification = await VerifyIdentityCoreAsync(cancellationToken).ConfigureAwait(false);
            switch (verification)
            {
                case IdentityOutcome.Mismatch:
                    return SessionRestoreResult.Expired("auth.identity_mismatch");
                case IdentityOutcome.SessionExpired:
                    return SessionRestoreResult.Expired("auth.session_expired");
                case IdentityOutcome.Offline:
                    return SessionRestoreResult.SignedIn(_session.UserId, offline: true);
                case IdentityOutcome.Verified:
                default:
                    return SessionRestoreResult.SignedIn(_session.UserId, refreshedOffline);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns a usable access token, refreshing once when it is near expiry.
    /// <see langword="null"/> means the session is no longer usable and the
    /// caller must treat the operation as unauthenticated.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                return null;
            }

            if (!NeedsRefresh(_session))
            {
                return _session.AccessToken;
            }

            var outcome = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            return outcome == RefreshOutcome.Succeeded ? _session?.AccessToken : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RefreshOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears tokens after a confirmed 401. Never logs the credential.</summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken) => await InvalidateAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The stable per-form-factor device id sent with every change push.</summary>
    public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) =>
        _tokens.GetOrCreateDeviceIdAsync(cancellationToken);

    private bool NeedsRefresh(StoredSession session) =>
        session.AccessTokenExpiresAt - ExpirySkew <= _time.GetUtcNow();

    private async Task<RefreshOutcome> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_api is null || _session is null)
        {
            return RefreshOutcome.SessionExpired;
        }

        var response = await _api
            .PostAsync<SessionDto>("v1/auth/refresh", new RefreshRequestDto(_session.RefreshToken), _session.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsCancelled)
        {
            return RefreshOutcome.Offline;
        }

        if (response.IsTransportFailure)
        {
            return RefreshOutcome.Offline;
        }

        if (response.IsUnauthorized || response.StatusCode is 403 or 422 && response.Error?.Code is WireErrorCodes.AuthInvalidRefresh or WireErrorCodes.AuthRefreshReused)
        {
            await ClearAsync(cancellationToken).ConfigureAwait(false);
            return RefreshOutcome.SessionExpired;
        }

        if (!response.IsSuccess || response.Value is null)
        {
            return response.StatusCode >= 400 && response.StatusCode < 500
                ? RefreshOutcome.SessionExpired
                : RefreshOutcome.Offline;
        }

        var session = response.Value;
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            await ClearAsync(cancellationToken).ConfigureAwait(false);
            return RefreshOutcome.SessionExpired;
        }

        var now = _time.GetUtcNow();
        var updated = _session with
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            AccessTokenExpiresAt = session.AccessTokenExpiresAt ?? now.Add(DefaultExpiry(session.ExpiresIn)),
            RefreshTokenExpiresAt = session.RefreshTokenExpiresAt ?? _session.RefreshTokenExpiresAt,
        };

        try
        {
            await _tokens.SaveSessionAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStorageException)
        {
            return RefreshOutcome.SessionExpired;
        }

        _session = updated;
        return RefreshOutcome.Succeeded;
    }

    private async Task<IdentityOutcome> VerifyIdentityCoreAsync(CancellationToken cancellationToken)
    {
        if (_api is null || _session is null)
        {
            return IdentityOutcome.SessionExpired;
        }

        var response = await _api
            .GetAsync<MeDto>("v1/auth/me", _session.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsCancelled || response.IsTransportFailure)
        {
            return IdentityOutcome.Offline;
        }

        if (response.IsUnauthorized)
        {
            var refresh = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (refresh == RefreshOutcome.Offline)
            {
                return IdentityOutcome.Offline;
            }

            if (refresh != RefreshOutcome.Succeeded || _session is null)
            {
                return IdentityOutcome.SessionExpired;
            }

            response = await _api
                .GetAsync<MeDto>("v1/auth/me", _session.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsCancelled || response.IsTransportFailure)
            {
                return IdentityOutcome.Offline;
            }

            if (response.IsUnauthorized)
            {
                await ClearAsync(cancellationToken).ConfigureAwait(false);
                return IdentityOutcome.SessionExpired;
            }
        }

        var id = response.Value?.User?.Id ?? response.Value?.Account?.UserId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return response.IsSuccess ? IdentityOutcome.Mismatch : IdentityOutcome.Offline;
        }

        if (!string.Equals(id, _session.UserId, StringComparison.Ordinal))
        {
            await ClearAsync(cancellationToken).ConfigureAwait(false);
            return IdentityOutcome.Mismatch;
        }

        return IdentityOutcome.Verified;
    }

    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        var origin = _session?.ServerOrigin ?? _configuration?.NormalizedOrigin;
        _session = null;
        if (origin is null)
        {
            return;
        }

        try
        {
            await _tokens.DeleteSessionAsync(origin, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStorageException)
        {
            // Reported as expired either way; a delete failure never re-exposes the token.
        }
    }

    private static TimeSpan DefaultExpiry(long expiresInSeconds) =>
        expiresInSeconds > 0 ? TimeSpan.FromSeconds(expiresInSeconds) : TimeSpan.FromMinutes(15);
}

public enum RefreshOutcome
{
    Succeeded = 0,
    SessionExpired,
    Offline,
}

public enum IdentityOutcome
{
    Verified = 0,
    Mismatch,
    SessionExpired,
    Offline,
}

/// <summary>Shape of <c>GET /v1/auth/me</c>.</summary>
public sealed record MeDto
{
    public UserDto? User { get; init; }

    public AccountDto? Account { get; init; }
}