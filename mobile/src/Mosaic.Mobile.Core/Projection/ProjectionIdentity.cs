using System.Security.Cryptography;
using System.Text;

namespace Mosaic.Mobile.Core.Projection;

/// <summary>
/// Verified account identity used to partition rebuilt projections. The owner
/// key is <c>sha256(normalizedOrigin + NUL + userId)</c> in lowercase hex; the
/// file name contains no server, account or email.
/// </summary>
public sealed record ProjectionIdentity
{
    private ProjectionIdentity(string serverOrigin, string userId, string ownerKey)
    {
        ServerOrigin = serverOrigin;
        UserId = userId;
        OwnerKey = ownerKey;
    }

    public string ServerOrigin { get; }

    public string UserId { get; }

    public string OwnerKey { get; }

    public static ProjectionIdentity Create(string serverOrigin, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverOrigin);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var material = Encoding.UTF8.GetBytes(serverOrigin + "\0" + userId);
        var hash = SHA256.HashData(material);
        return new ProjectionIdentity(serverOrigin, userId, Convert.ToHexString(hash).ToLowerInvariant());
    }
}