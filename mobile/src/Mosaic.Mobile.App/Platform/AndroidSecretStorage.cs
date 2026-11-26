using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Mosaic.Mobile.Core.Auth;

namespace Mosaic.Mobile.App.Platform;

/// <summary>
/// Android Keystore backed byte storage. Every record is AES/GCM encrypted with
/// a device-bound key that never leaves the keystore; ciphertext lives in a
/// private SharedPreferences file, and the app disables platform backup
/// entirely. The file name and key alias are new, so records written by the
/// deleted 0.2.x app are never read.
/// </summary>
public sealed class AndroidSecretStorage : ISecretStorage
{
    public const string PreferencesFile = "mosaic.mobile.v3.secure";

    public const string KeyAlias = "mosaic.mobile.v3.secret";

    private const string Transformation = "AES/GCM/NoPadding";

    private const int IvBytes = 12;

    private static readonly object KeyGate = new();

    private readonly ISharedPreferences _preferences;

    public AndroidSecretStorage(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _preferences = context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Private preferences are unavailable.");
    }

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var stored = _preferences.GetString(key, null);
        if (string.IsNullOrEmpty(stored))
        {
            return Task.FromResult<byte[]?>(null);
        }

        try
        {
            return Task.FromResult<byte[]?>(Decrypt(Convert.FromBase64String(stored)));
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or FormatException or InvalidOperationException)
        {
            // A record that cannot be decrypted is dropped rather than surfaced.
            _preferences.Edit()?.Remove(key)?.Apply();
            return Task.FromResult<byte[]?>(null);
        }
    }

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var cipherText = Encrypt(value);
        var editor = _preferences.Edit() ?? throw new InvalidOperationException("Preferences editor unavailable.");
        editor.PutString(key, Convert.ToBase64String(cipherText));
        if (!editor.Commit())
        {
            throw new InvalidOperationException("Secure storage write was rejected.");
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var editor = _preferences.Edit() ?? throw new InvalidOperationException("Preferences editor unavailable.");
        editor.Remove(key);
        editor.Commit();
        return Task.CompletedTask;
    }

    /// <summary>Removes the 0.2.x keystore entry so its ciphertext can never be read again.</summary>
    public static void PurgeLegacyKey()
    {
        const string legacyAlias = "mosaic.mobile.session.v1";
        try
        {
            var store = KeyStore.GetInstance("AndroidKeyStore");
            store?.Load(null);
            if (store?.ContainsAlias(legacyAlias) == true)
            {
                store.DeleteEntry(legacyAlias);
            }
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or InvalidOperationException)
        {
            // Best effort; the new client never reads that alias anyway.
        }
    }

    private static byte[] Encrypt(byte[] plain)
    {
        var key = GetOrCreateKey();
        var cipher = Cipher.GetInstance(Transformation)
            ?? throw new InvalidOperationException("AES/GCM is unavailable.");
        cipher.Init(CipherMode.EncryptMode, key);
        var iv = cipher.GetIV() ?? throw new InvalidOperationException("Cipher IV missing.");
        var encrypted = cipher.DoFinal(plain) ?? throw new InvalidOperationException("Encryption failed.");

        var result = new byte[1 + iv.Length + encrypted.Length];
        result[0] = 1;
        Buffer.BlockCopy(iv, 0, result, 1, iv.Length);
        Buffer.BlockCopy(encrypted, 0, result, 1 + iv.Length, encrypted.Length);
        return result;
    }

    private static byte[] Decrypt(byte[] value)
    {
        if (value.Length <= 1 + IvBytes || value[0] != 1)
        {
            throw new InvalidOperationException("Unknown secure record format.");
        }

        var iv = new byte[IvBytes];
        Buffer.BlockCopy(value, 1, iv, 0, IvBytes);
        var payload = new byte[value.Length - 1 - IvBytes];
        Buffer.BlockCopy(value, 1 + IvBytes, payload, 0, payload.Length);

        var key = GetOrCreateKey();
        var cipher = Cipher.GetInstance(Transformation)
            ?? throw new InvalidOperationException("AES/GCM is unavailable.");
        cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
        return cipher.DoFinal(payload) ?? throw new InvalidOperationException("Decryption failed.");
    }

    private static IKey GetOrCreateKey()
    {
        lock (KeyGate)
        {
            var store = KeyStore.GetInstance("AndroidKeyStore")
                ?? throw new InvalidOperationException("AndroidKeyStore is unavailable.");
            store.Load(null);
            if (store.GetKey(KeyAlias, null) is { } existing)
            {
                return existing;
            }

            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
                ?? throw new InvalidOperationException("AES key generation is unavailable.");
            var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetRandomizedEncryptionRequired(true)
                .Build();
            generator.Init(spec);
            return generator.GenerateKey() ?? throw new InvalidOperationException("Key generation failed.");
        }
    }
}