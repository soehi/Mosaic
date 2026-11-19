using System.Globalization;
using System.Text;

namespace Mosaic.Mobile.Auth;

/// <summary>
/// Local input rules that mirror the server (rune counts, not UTF-16 units).
/// They are advisory: the server stays authoritative and its
/// <c>auth.weak_password</c> / <c>auth.invalid_email</c> answers win.
/// </summary>
public static class AuthInputRules
{
    public const int MinPasswordLength = 10;
    public const int MaxPasswordLength = 256;
    public const int MaxEmailLength = 254;

    public static int CountRunes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    public static bool IsPasswordLengthValid(string? password)
    {
        var length = CountRunes(password);
        return length is >= MinPasswordLength and <= MaxPasswordLength;
    }

    public static bool IsEmailShapeValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength)
        {
            return false;
        }

        var trimmed = email.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return false;
        }

        var domain = trimmed[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal)
            && !domain.StartsWith('.')
            && !domain.EndsWith('.');
    }

    public static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    /// <summary>Six-digit code as used by email verification and code login.</summary>
    public static bool IsEmailCodeShapeValid(string? code, int codeLength = 6)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Trim();
        if (trimmed.Length != codeLength)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    public static string FormatCodeForDisplay(string? userCode)
    {
        var normalized = DeviceUserCodes.Normalize(userCode);
        if (normalized is null)
        {
            return string.Empty;
        }

        const int split = 4;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{normalized[..split]}-{normalized[split..]}");
    }
}

public static class DeviceUserCodes
{
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ23456789";

    /// <summary>
    /// Mirrors the server: trim, upper-case, drop hyphens and spaces, then
    /// require exactly eight characters from the unambiguous alphabet. Invalid
    /// input stays invalid; the client never guesses further.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value
            .Trim()
            .ToUpperInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (compact.Length != 8)
        {
            return null;
        }

        foreach (var character in compact)
        {
            if (!Alphabet.Contains(character, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return compact;
    }

    public static string Compact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}
