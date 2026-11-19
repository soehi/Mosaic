using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mosaic.Mobile.Auth;

/// <summary>
/// Encrypt-on-device token persistence. One record per (server, account slot);
/// switching servers cannot read another server's record. A corrupted or
/// undecryptable record is deleted and reported as
/// <see cref="SessionStoreStatus.Unavailable"/>, which the UI renders as
/// "session unavailable, sign in again" — the offline cache stays untouched.
/// </summary>
public sealed class TokenStore
{
    private const string SessionKeyPrefix = "mosaic.session.";
    private const string DeviceKey = "mosaic.device";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ISecretStorage _storage;
    private readonly TimeProvider _time;

    public TokenStore(ISecretStorage storage, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SessionLoadResult> LoadAsync(string serverUrl, CancellationToken cancellationToken)
    {
        try
        {
            var key = SessionKeyFor(serverUrl);
            var bytes = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return SessionLoadResult.Missing;
            }

            var session = JsonSerializer.Deserialize<StoredSession>(bytes, SerializerOptions);
            if (session is null
                || string.IsNullOrWhiteSpace(session.RefreshToken)
                || string.IsNullOrWhiteSpace(session.DeviceId))
            {
                await SafeDeleteAsync(key, cancellationToken).ConfigureAwait(false);
                return SessionLoadResult.Unavailable("session_record_invalid");
            }

            if (!string.Equals(session.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
            {
                // The record belongs to another server: never reuse it.
                await SafeDeleteAsync(key, cancellationToken).ConfigureAwait(false);
                return SessionLoadResult.Missing;
            }

            return SessionLoadResult.Loaded(session);
        }
        catch (SessionStoreUnavailableException)
        {
            return SessionLoadResult.Unavailable("secure_storage_unavailable");
        }
        catch (JsonException)
        {
            await SafeDeleteAsync(SessionKeyFor(serverUrl), cancellationToken).ConfigureAwait(false);
            return SessionLoadResult.Unavailable("session_record_corrupt");
        }
        catch (CryptographicException)
        {
            await SafeDeleteAsync(SessionKeyFor(serverUrl), cancellationToken).ConfigureAwait(false);
            return SessionLoadResult.Unavailable("session_record_undecryptable");
        }
    }

    public async Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            session with { SavedAtUtc = _time.GetUtcNow() },
            SerializerOptions);
        try
        {
            await _storage.WriteAsync(SessionKeyFor(session.ServerUrl), bytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SessionStoreUnavailableException("secure_storage_write_failed", exception);
        }
    }

    public async Task ClearAsync(string serverUrl, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(SessionKeyFor(serverUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SessionStoreUnavailableException("secure_storage_delete_failed", exception);
        }
    }

    /// <summary>
    /// Device identity survives reinstall only through platform backup, which is
    /// disabled for the token record; a fresh install gets a new id.
    /// </summary>
    public async Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _storage.ReadAsync(DeviceKey, cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var record = JsonSerializer.Deserialize<DeviceRecord>(bytes, SerializerOptions);
                if (record is not null && Guid.TryParse(record.DeviceId, out _))
                {
                    return record.DeviceId;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SessionStoreUnavailableException("device_id_read_failed", exception);
        }

        var deviceId = Guid.NewGuid().ToString("D");
        try
        {
            await _storage.WriteAsync(
                    DeviceKey,
                    JsonSerializer.SerializeToUtf8Bytes(new DeviceRecord(deviceId), SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SessionStoreUnavailableException("device_id_write_failed", exception);
        }

        return deviceId;
    }

    private static string SessionKeyFor(string serverUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.Trim().ToLowerInvariant()));
        return SessionKeyPrefix + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private async Task SafeDeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: a broken record that cannot be removed is still
            // reported as unavailable, never surfaced as a credential.
        }
    }

    private sealed record DeviceRecord(string DeviceId);
}
