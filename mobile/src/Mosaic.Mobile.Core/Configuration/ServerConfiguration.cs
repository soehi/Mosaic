namespace Mosaic.Mobile.Core.Configuration;

/// <summary>
/// A resolved server target. <see cref="NormalizedOrigin"/> is the identity
/// input for the projection partition and never contains credentials, a query
/// or a fragment.
/// </summary>
public sealed record ServerConfiguration(Uri BaseUri, string NormalizedOrigin, bool IsLoopbackDevelopment)
{
    public string DeviceLabel { get; init; } = "Mosaic 手机端";
}

/// <summary>
/// The replaceable connection boundary. The real provisioning channel is
/// deferred by the refactor plan; the kernel only depends on this interface.
/// </summary>
public interface IServerConfigProvider
{
    /// <summary>Returns <see langword="null"/> when no server has been provisioned yet.</summary>
    Task<ServerConfiguration?> GetAsync(CancellationToken cancellationToken);
}