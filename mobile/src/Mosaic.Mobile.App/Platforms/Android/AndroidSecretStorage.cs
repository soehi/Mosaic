using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Mosaic.Mobile.Auth;

namespace Mosaic.Mobile.App.Platforms.Android;

/// <summary>
/// AES-GCM with a non-exportable key in the Android Keystore. Ciphertext lives
/// in a private SharedPreferences file that is excluded from backup; the key
/// name is used as associated data, so a value cannot be moved between slots.
/// Any keystore failure becomes "session unavailable", never plaintext.
/// </summary>
public sealed class AndroidSecretStorage : ISecretStorage
{
    private const string PreferencesFile = "mosaic_secure";
    private const string KeyAlias = "mosaic.mobile.session.v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;

    private readonly ISharedPreferences _preferences;

    public AndroidSecretStorage(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _preferences = context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private)
            ?? throw new SessionStoreUnavailableException("shared preferences unavailable");
    }

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken) =>
        Task.Run(() => Read(key), cancellationToken);

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken) =>
        Task.Run(() => Write(key, value), cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        Task.Run(() => Delete(key), cancellationToken);

    private byte[]? Read(string key)
    {
        try
        {
            var stored = _preferences.GetString(key, null);
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            var payload = Convert.FromBase64String(stored);
            var ivLength = payload[0];
            if (payload.Length <= ivLength + 1)
            {
                throw new SessionStoreUnavailableException("stored secret is malformed");
            }

            var iv = payload.AsSpan(1, ivLength).ToArray();
            var ciphertext = payload.AsSpan(1 + ivLength).ToArray();

            var cipher = Cipher.GetInstance(Transformation)
                ?? throw new SessionStoreUnavailableException("cipher unavailable");
            cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, iv));
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
            return cipher.DoFinal(ciphertext);
        }
        catch (SessionStoreUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Java.Lang.Exception or FormatException)
        {
            throw new SessionStoreUnavailableException("keystore read failed", exception);
        }
    }

    private void Write(string key, byte[] value)
    {
        try
        {
            var cipher = Cipher.GetInstance(Transformation)
                ?? throw new SessionStoreUnavailableException("cipher unavailable");
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
            var iv = cipher.GetIV() ?? throw new SessionStoreUnavailableException("missing IV");
            var ciphertext = cipher.DoFinal(value)
                ?? throw new SessionStoreUnavailableException("cipher produced no ciphertext");

            var payload = new byte[1 + iv.Length + ciphertext.Length];
            payload[0] = (byte)iv.Length;
            iv.CopyTo(payload.AsSpan(1));
            ciphertext.CopyTo(payload.AsSpan(1 + iv.Length));

            var committed = _preferences.Edit()?
                .PutString(key, Convert.ToBase64String(payload))?
                .Commit() ?? false;
            if (!committed)
            {
                throw new SessionStoreUnavailableException("preferences commit failed");
            }
        }
        catch (SessionStoreUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Java.Lang.Exception)
        {
            throw new SessionStoreUnavailableException("keystore write failed", exception);
        }
    }

    private void Delete(string key)
    {
        _preferences.Edit()?.Remove(key)?.Commit();
    }

    private static ISecretKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore")
            ?? throw new SessionStoreUnavailableException("keystore unavailable");
        keyStore.Load(null);
        if (keyStore.GetKey(KeyAlias, null) is ISecretKey existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
            ?? throw new SessionStoreUnavailableException("key generator unavailable");
        var spec = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(spec);
        generator.GenerateKey();

        return keyStore.GetKey(KeyAlias, null) as ISecretKey
            ?? throw new SessionStoreUnavailableException("keystore key creation failed");
    }
}
