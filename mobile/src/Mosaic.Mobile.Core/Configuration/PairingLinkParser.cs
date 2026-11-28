using System;

namespace Mosaic.Mobile.Core.Configuration;

/// <summary>
/// A validated desktop pairing target. The fragment is the 43-character one-time
/// reference; it never travels in an HTTP request, so copying or typing it is
/// equivalent to scanning the desktop QR image.
/// </summary>
public sealed record PairingLink(Uri Origin, string Reference, Uri ConnectUri);

public static class PairingLinkParser
{
    public const int ReferenceLength = 43;

    /// <summary>
    /// Accepts either the full App Link or the bare 43-character reference the
    /// desktop QR encodes. Every rejection path leaves no session state behind.
    /// </summary>
    public static bool TryParse(string? input, string expectedHost, out PairingLink link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(expectedHost))
        {
            return false;
        }

        var text = input.Trim();
        if (text.Length == ReferenceLength)
        {
            if (!IsReference(text))
            {
                return false;
            }
            return TryBuild(new Uri("https://" + expectedHost), text, out link);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || !string.Equals(uri.IdnHost, expectedHost, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/device/mobile/connect" || uri.Query.Length != 0
            || uri.Fragment is not { Length: ReferenceLength + 1 } fragment
            || !IsReference(fragment[1..]))
        {
            return false;
        }

        return TryBuild(new Uri(uri.GetLeftPart(UriPartial.Authority)), fragment[1..], out link);
    }

    private static bool TryBuild(Uri origin, string reference, out PairingLink link)
    {
        link = new(origin, reference, new Uri(origin, "/device/mobile/connect#" + reference));
        return true;
    }

    private static bool IsReference(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
