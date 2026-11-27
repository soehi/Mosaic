using System.Diagnostics.CodeAnalysis;

namespace Mosaic.Mobile.Core.Configuration;

/// <summary>
/// Normalizes a user-supplied server address. HTTPS is mandatory; plain HTTP is
/// accepted only for loopback development hosts, and there is no option to
/// ignore a certificate error at any point.
/// </summary>
public static class ServerAddress
{
    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out ServerConfiguration? configuration)
    {
        configuration = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var loopback = IsLoopback(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !(loopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var path = NormalizePath(uri.AbsolutePath);

        // The base URI keeps a trailing slash so relative "v1/..." requests
        // resolve under a path-prefixed deployment; the identity string drops it
        // so the same server always hashes to the same owner key.
        var builder = new UriBuilder(uri)
        {
            Path = path.EndsWith('/') ? path : path + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        var baseUri = builder.Uri;
        var origin = baseUri.AbsoluteUri.TrimEnd('/');
        configuration = new ServerConfiguration(baseUri, origin, loopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(uri.Host, "[::1]", StringComparison.Ordinal)
        || string.Equals(uri.Host, "::1", StringComparison.Ordinal);

    private static string NormalizePath(string path)
    {
        var trimmed = path.TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) ? "/" : trimmed;
    }
}