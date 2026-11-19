namespace Mosaic.Mobile.Auth;

/// <summary>
/// Validates and normalizes the user supplied server base URL. HTTPS is
/// mandatory; plain HTTP is only accepted for a loopback address so a
/// developer can point the app at the local server, and that rule is applied
/// identically in every build — the app never silently downgrades a real host.
/// </summary>
public static class ServerAddress
{
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "server.url_required";
            return false;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            error = "server.url_invalid";
            return false;
        }

        if (uri.Scheme is not ("https" or "http"))
        {
            error = "server.url_scheme";
            return false;
        }

        if (uri.Scheme == "http" && !IsLoopback(uri))
        {
            error = "server.url_https_required";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "server.url_invalid";
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        var baseUrl = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^3].TrimEnd('/');
        }

        normalized = baseUrl;
        return true;
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "10.0.2.2", StringComparison.Ordinal);
}
