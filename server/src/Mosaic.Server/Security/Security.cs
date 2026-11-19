using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Mosaic.Server.Http;

namespace Mosaic.Server.Security;

public static class PasswordHasher
{
    public const int Iterations = 310_000;

    public const int SaltBytes = 16;

    public const int KeyBytes = 32;

    public static (byte[] Salt, byte[] Hash) Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt);
        return (salt, hash);
    }

    public static bool Verify(string password, byte[] salt, byte[] expected)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(expected);
        var actual = Derive(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
}

public static class PasswordPolicy
{
    public const int MinLength = 10;

    public const int MaxLength = 256;

    public static void Validate(string? password)
    {
        if (password is null)
        {
            throw Invalid();
        }

        var length = password.EnumerateRunes().Count();
        if (length < MinLength || length > MaxLength)
        {
            throw Invalid();
        }
    }

    private static ApiException Invalid() =>
        new(422, "auth.weak_password", $"密码需要 {MinLength} 至 {MaxLength} 个字符。");
}

public static class Tokens
{
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ23456789";

    public static string Create(int bytes = 32) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    public static string CreateUserCode()
    {
        var chars = new char[9];
        for (var index = 0; index < 8; index++)
        {
            chars[index < 4 ? index : index + 1] =
                UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }

        chars[4] = '-';
        return new string(chars);
    }

    /// <summary>Returns the canonical 8-character form or null when the code is malformed.</summary>
    public static string? NormalizeUserCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value.Trim().ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length != 8)
        {
            return null;
        }

        foreach (var character in compact)
        {
            if (!UserCodeAlphabet.Contains(character, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return compact;
    }

    public static string FormatUserCode(string compact) =>
        compact.Length == 8 ? compact[..4] + "-" + compact[4..] : compact;

    public static string CreateEmailCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class SecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null,
            info: Encoding.UTF8.GetBytes("mosaic-server/secrets/v1"));
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        var payload = new byte[nonce.Length + cipher.Length + tag.Length];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, nonce.Length);
        tag.CopyTo(payload, nonce.Length + cipher.Length);
        return Base64Url.EncodeToString(payload);
    }

    public string Decrypt(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = Base64Url.DecodeFromChars(payload);
        if (bytes.Length < 12 + 16)
        {
            throw new CryptographicException("密文长度无效。");
        }

        var nonce = bytes.AsSpan(0, 12);
        var tag = bytes.AsSpan(bytes.Length - 16, 16);
        var cipher = bytes.AsSpan(12, bytes.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Stable, peppered hashes used for identifiers in storage and logs.</summary>
public sealed class SecretHasher
{
    private readonly byte[] _emailKey;
    private readonly byte[] _codeKey;
    private readonly byte[] _ipKey;

    public SecretHasher(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        _emailKey = Derive(masterKey, "email");
        _codeKey = Derive(masterKey, "email-code");
        _ipKey = Derive(masterKey, "client-ip");
    }

    public string EmailHash(string normalizedEmail) =>
        Hmac(_emailKey, Encoding.UTF8.GetBytes(normalizedEmail));

    public string CodeHash(string emailHash, string purpose, string code) =>
        Hmac(_codeKey, Encoding.UTF8.GetBytes(emailHash + ":" + purpose + ":" + code));

    public string IpHash(string? address) =>
        Hmac(_ipKey, Encoding.UTF8.GetBytes(string.IsNullOrEmpty(address) ? "unknown" : address));

    public string UserCodeHash(string compactUserCode) =>
        Hmac(_codeKey, Encoding.UTF8.GetBytes("device-user:" + compactUserCode));

    public string DeviceCodeHash(string deviceCode) =>
        Hmac(_codeKey, Encoding.UTF8.GetBytes("device-code:" + deviceCode));

    public bool FixedTimeEquals(string left, string right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static byte[] Derive(byte[] masterKey, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null,
            info: Encoding.UTF8.GetBytes("mosaic-server/" + purpose + "/v1"));

    private static string Hmac(byte[] key, byte[] data) =>
        Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
}
