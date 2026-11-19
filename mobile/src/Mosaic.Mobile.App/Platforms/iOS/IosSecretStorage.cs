using Foundation;
using Mosaic.Mobile.Auth;
using Security;

namespace Mosaic.Mobile.App.Platforms.iOS;

/// <summary>
/// Keychain entries with <c>AfterFirstUnlockThisDeviceOnly</c>: readable when
/// the app resumes without the device being unlocked interactively, never
/// migrated to another device through backups.
/// </summary>
public sealed class IosSecretStorage : ISecretStorage
{
    private const string Service = "app.mosaic.tasks.session";

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken) =>
        Task.Run(() => Read(key), cancellationToken);

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken) =>
        Task.Run(() => Write(key, value), cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        Task.Run(() => Delete(key), cancellationToken);

    private static byte[]? Read(string key)
    {
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = key,
        };
        var status = SecKeyChain.QueryAsRecord(query, out var result);
        using (result)
        {
            if (status == SecStatusCode.ItemNotFound)
            {
                return null;
            }

            if (status != SecStatusCode.Success || result?.ValueData is null)
            {
                throw new SessionStoreUnavailableException($"keychain read failed: {status}");
            }

            return result.ValueData.ToArray();
        }
    }

    private static void Write(string key, byte[] value)
    {
        Delete(key);
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = key,
            ValueData = NSData.FromArray(value),
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
        };
        var status = SecKeyChain.Add(record);
        if (status is not (SecStatusCode.Success or SecStatusCode.DuplicateItem))
        {
            throw new SessionStoreUnavailableException($"keychain write failed: {status}");
        }
    }

    private static void Delete(string key)
    {
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = key,
        };
        var status = SecKeyChain.Remove(record);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
        {
            throw new SessionStoreUnavailableException($"keychain delete failed: {status}");
        }
    }
}
