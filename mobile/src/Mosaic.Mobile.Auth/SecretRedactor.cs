using System.Text.RegularExpressions;

namespace Mosaic.Mobile.Auth;

/// <summary>
/// Defence in depth for any text that reaches an exception message, a crash
/// report or a debug log: emails, bearer/JWT-like tokens, reset codes and the
/// eight-character device code are replaced before the text leaves the app.
/// Normal operation never logs request or response bodies.
/// </summary>
public static partial class SecretRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = EmailPattern().Replace(text, "[email]");
        redacted = BearerPattern().Replace(redacted, "[token]");
        redacted = LongTokenPattern().Replace(redacted, "[token]");
        redacted = DeviceCodePattern().Replace(redacted, m => "[device-code]");
        redacted = SixDigitPattern().Replace(redacted, "[code]");
        return redacted;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{20,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex LongTokenPattern();

    [GeneratedRegex(@"\b[BCDFGHJKLMNPQRSTVWXZ23456789]{4}-[BCDFGHJKLMNPQRSTVWXZ23456789]{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCodePattern();

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigitPattern();
}
