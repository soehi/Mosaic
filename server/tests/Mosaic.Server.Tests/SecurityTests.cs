using System.Security.Cryptography;
using System.Text;
using Mosaic.Server.Http;
using Mosaic.Server.Security;

namespace Mosaic.Server.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Password_hashes_use_documented_pbkdf2_parameters()
    {
        Assert.Equal(310_000, PasswordHasher.Iterations);
        Assert.Equal(16, PasswordHasher.SaltBytes);
        Assert.Equal(32, PasswordHasher.KeyBytes);

        var (salt, hash) = PasswordHasher.Hash("correct horse battery staple");
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);

        var recomputed = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes("correct horse battery staple"),
            salt,
            310_000,
            HashAlgorithmName.SHA256,
            32);
        Assert.True(CryptographicOperations.FixedTimeEquals(hash, recomputed));
        Assert.True(PasswordHasher.Verify("correct horse battery staple", salt, hash));
        Assert.False(PasswordHasher.Verify("wrong password value", salt, hash));

        var (secondSalt, secondHash) = PasswordHasher.Hash("correct horse battery staple");
        Assert.NotEqual(salt, secondSalt);
        Assert.NotEqual(hash, secondHash);
    }

    [Theory]
    [InlineData("  Foo.Bar+tag@Example.COM ", "foo.bar+tag@example.com")]
    [InlineData("a.b@sub.example.org", "a.b@sub.example.org")]
    public void Email_normalizer_lowercases_and_trims(string input, string expected) =>
        Assert.Equal(expected, EmailNormalizer.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alice")]
    [InlineData("alice@localhost")]
    [InlineData("alice@@example.com")]
    [InlineData("alice b@example.com")]
    [InlineData(".alice@example.com")]
    [InlineData("alice@example..com")]
    [InlineData("ali\nce@example.com")]
    public void Email_normalizer_rejects_invalid_addresses(string input) =>
        Assert.Throws<ApiException>(() => EmailNormalizer.Normalize(input));

    [Fact]
    public void User_codes_are_eight_unambiguous_characters()
    {
        for (var index = 0; index < 200; index++)
        {
            var code = Tokens.CreateUserCode();
            Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ23456789]{4}-[BCDFGHJKLMNPQRSTVWXZ23456789]{4}$", code);
            Assert.Equal(code.Replace("-", string.Empty, StringComparison.Ordinal),
                Tokens.NormalizeUserCode(code));
        }

        Assert.Null(Tokens.NormalizeUserCode("AAAA-AAAA"));
        Assert.Null(Tokens.NormalizeUserCode("ABCD"));
    }

    [Fact]
    public void Email_codes_are_six_digits()
    {
        for (var index = 0; index < 100; index++)
        {
            Assert.Matches("^\\d{6}$", Tokens.CreateEmailCode());
        }
    }

    [Fact]
    public void Secret_protector_round_trips_and_rejects_tampering()
    {
        var protector = new SecretProtector(RandomNumberGenerator.GetBytes(32));
        var cipher = protector.Encrypt("smtp-password-密码");
        Assert.NotEqual("smtp-password-密码", cipher);
        Assert.Equal("smtp-password-密码", protector.Decrypt(cipher));

        var bytes = Convert.FromBase64String(Pad(cipher));
        bytes[20] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.ThrowsAny<CryptographicException>(() => protector.Decrypt(tampered));
    }

    private static string Pad(string base64Url)
    {
        var value = base64Url.Replace('-', '+').Replace('_', '/');
        return value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
    }
}
