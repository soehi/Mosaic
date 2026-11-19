using System.Net.Mail;

namespace Mosaic.Server.Http;

public static class EmailNormalizer
{
    public const int MaxLength = 254;

    /// <summary>
    /// Trims, lowercases and validates a basic mailbox address. No provider-specific
    /// rewriting (no Gmail dot or plus stripping): the same address always maps to
    /// the same account, and different addresses never collide.
    /// </summary>
    public static string Normalize(string? value)
    {
        var email = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length is 0 or > MaxLength)
        {
            throw Invalid();
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
        {
            throw Invalid();
        }

        var local = email[..at];
        var domain = email[(at + 1)..];
        if (local.Length > 64 || domain.Length > 253 || !domain.Contains('.', StringComparison.Ordinal))
        {
            throw Invalid();
        }

        if (local.StartsWith(".", StringComparison.Ordinal) || local.EndsWith(".", StringComparison.Ordinal)
            || domain.StartsWith(".", StringComparison.Ordinal) || domain.EndsWith(".", StringComparison.Ordinal)
            || email.Contains("..", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        foreach (var character in email)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                throw Invalid();
            }
        }

        try
        {
            var parsed = new MailAddress(email);
            if (!string.Equals(parsed.Address, email, StringComparison.Ordinal))
            {
                throw Invalid();
            }
        }
        catch (FormatException)
        {
            throw Invalid();
        }

        return email;
    }

    public static bool IsValid(string? value)
    {
        try
        {
            Normalize(value);
            return true;
        }
        catch (ApiException)
        {
            return false;
        }
    }

    private static ApiException Invalid() =>
        new(422, "auth.invalid_email", "请填写有效的邮箱地址。");
}
