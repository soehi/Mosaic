using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Auth;

/// <summary>
/// Encrypt-on-device persistence for one session record per server origin and a
/// stable device id. Records are stored as JSON through <see cref="ISecretStorage"/>;
/// keys carry only a hash prefix so the file listing never shows a server,
/// account or email.
/// </summary>
public sealed class TokenStore
{
    private readonly ISecretStorage _storage;

    public TokenStore(ISecretStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<StoredSession?> LoadSessionAsync(string serverOrigin, CancellationToken cancellationToken)
    {
        var key = SessionKey(serverOrigin);
        try
        {
            var bytes = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is not { Length: > 0 })
            {
                return null;
            }

            var session = JsonSerializer.Deserialize<StoredSession>(bytes, WireJson.Options);
            if (session is null
                || string.IsNullOrWhiteSpace(session.UserId)
                || string.IsNullOrWhiteSpace(session.RefreshToken)
                || !string.Equals(session.ServerOrigin, serverOrigin, StringComparison.Ordinal))
            {
                await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return session;
        }
        catch (JsonException)
        {
            await TryDeleteAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecretStorageException("read_session", exception);
        }
    }

    public async Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var key = SessionKey(session.ServerOrigin);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(session, WireJson.Options);
            await _storage.WriteAsync(key, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecretStorageException("write_session", exception);
        }
    }

    public async Task DeleteSessionAsync(string serverOrigin, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(SessionKey(serverOrigin), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecretStorageException("delete_session", exception);
        }
    }

    public async Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _storage.ReadAsync(SecretStorageKeys.DeviceId, cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var existing = Encoding.UTF8.GetString(bytes);
                if (Guid.TryParseExact(existing, "N", out _))
                {
                    return existing;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecretStorageException("read_device", exception);
        }

        var created = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await _storage
                .WriteAsync(SecretStorageKeys.DeviceId, Encoding.UTF8.GetBytes(created), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecretStorageException("write_device", exception);
        }

        return created;
    }

    private async Task TryDeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken record that cannot be removed is still reported as missing,
            // never surfaced as a credential.
        }
    }

    private static string SessionKey(string serverOrigin)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serverOrigin));
        return SecretStorageKeys.SessionPrefix + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}