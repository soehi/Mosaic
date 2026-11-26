namespace Mosaic.Mobile.Auth;

/// <summary>
/// Narrow surface used by the sync engine. Implemented by
/// <see cref="MosaicApiClient"/> — the single owner of the HTTP stack — so Sync
/// never constructs its own client, URL or authorization header.
/// </summary>
public interface IMosaicApi
{
    Task<ApiResult<T>> GetAsync<T>(
        string relativePath,
        bool authenticated = true,
        bool retryTransient = false,
        CancellationToken cancellationToken = default);

    Task<ApiResult<T>> PostAsync<T>(
        string relativePath,
        object? body,
        bool authenticated = true,
        bool retryTransient = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raw GET with a hard byte ceiling. Used for snapshot downloads so a
    /// hostile or misconfigured server cannot exhaust mobile memory: the
    /// declared Content-Length is rejected up front and the streamed body is
    /// aborted the moment it crosses <paramref name="maxBytes"/>. Failures carry
    /// <see cref="Mosaic.Mobile.Wire.WireErrorCodes.ClientResponseTooLarge"/>.
    /// </summary>
    Task<ApiResult<byte[]>> GetBytesAsync(
        string relativePath,
        long maxBytes,
        bool authenticated = true,
        bool retryTransient = false,
        CancellationToken cancellationToken = default);
}
