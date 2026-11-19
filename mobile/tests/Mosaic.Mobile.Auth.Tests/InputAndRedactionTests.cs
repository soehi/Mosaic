using Mosaic.Mobile.Auth;

namespace Mosaic.Mobile.Auth.Tests;

public sealed class InputAndRedactionTests
{
    [Fact]
    public void Password_length_counts_runes_not_utf16_units()
    {
        var emojiPassword = string.Concat(Enumerable.Repeat("😀", 10));

        Assert.Equal(10, AuthInputRules.CountRunes(emojiPassword));
        Assert.True(AuthInputRules.IsPasswordLengthValid(emojiPassword));
        Assert.False(AuthInputRules.IsPasswordLengthValid(string.Concat(Enumerable.Repeat("😀", 9))));
        Assert.False(AuthInputRules.IsPasswordLengthValid(new string('a', 257)));
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData(" user@example.com ", true)]
    [InlineData("user@example", false)]
    [InlineData("user@@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("user example@example.com", false)]
    [InlineData("", false)]
    public void Email_shape_validation(string email, bool expected)
    {
        Assert.Equal(expected, AuthInputRules.IsEmailShapeValid(email));
    }

    [Fact]
    public void Email_is_normalized()
    {
        Assert.Equal("user@example.com", AuthInputRules.NormalizeEmail("  User@Example.COM "));
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("12345a", false)]
    [InlineData(" 123456 ", true)]
    public void Email_code_shape(string code, bool expected)
    {
        Assert.Equal(expected, AuthInputRules.IsEmailCodeShapeValid(code));
    }

    [Fact]
    public void Display_code_has_a_hyphen()
    {
        Assert.Equal("BCDF-GHJK", AuthInputRules.FormatCodeForDisplay("BCDFGHJK"));
    }

    [Fact]
    public void Redactor_removes_emails_tokens_and_codes()
    {
        var text =
            "login user@example.com failed with Bearer abcdefghijklmnopqrstuvwxyz0123456789 code 123456 device BCDF-GHJK token refresh-abcdefghijklmnop";

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain("user@example.com", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("BCDF-GHJK", redacted, StringComparison.Ordinal);
        Assert.Contains("[email]", redacted, StringComparison.Ordinal);
        Assert.Contains("[code]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_handles_empty_input()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }
}
