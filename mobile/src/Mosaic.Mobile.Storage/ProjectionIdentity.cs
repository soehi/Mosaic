using System.Security.Cryptography;
using System.Text;

namespace Mosaic.Mobile.Storage;

/// <summary>
/// Verified account identity used to partition rebuildable local projections.
/// Server origins and user ids never appear in projection file names.
/// </summary>
public sealed record ProjectionIdentity
{
    private ProjectionIdentity(string normalizedServerOrigin, string userId, string ownerKey)
    {
        NormalizedServerOrigin = normalizedServerOrigin;
        UserId = userId;
        OwnerKey = ownerKey;
    }

    public string NormalizedServerOrigin { get; }

    public string UserId { get; }

    public string OwnerKey { get; }

    public static ProjectionIdentity Create(string? normalizedServerOrigin, string? userId)
    {
        if (string.IsNullOrWhiteSpace(normalizedServerOrigin))
        {
            throw new ArgumentException("A normalized server origin is required.", nameof(normalizedServerOrigin));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A verified user id is required.", nameof(userId));
        }

        if (!Uri.TryCreate(normalizedServerOrigin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback
                && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "10.0.2.2", StringComparison.Ordinal))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "The server must be an absolute HTTPS base URL, or HTTP on loopback.",
                nameof(normalizedServerOrigin));
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3].TrimEnd('/');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\0" + userId));
        return new ProjectionIdentity(normalized, userId, Convert.ToHexStringLower(digest));
    }
}
