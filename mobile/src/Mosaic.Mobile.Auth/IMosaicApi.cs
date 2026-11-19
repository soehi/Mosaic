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
}
