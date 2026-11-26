using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Core.Api;

/// <summary>
/// One HTTP exchange. <see cref="StatusCode"/> is <c>0</c> for transport
/// failures and <c>-1</c> for caller cancellation; neither ever carries a body.
/// </summary>
public sealed record ApiResponse<T>(int StatusCode, T? Value, WireError? Error)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public bool IsTransportFailure => StatusCode == 0;

    public bool IsCancelled => StatusCode == -1;

    public bool IsUnauthorized => StatusCode == 401;

    public bool IsConflict => StatusCode == 409;

    public static ApiResponse<T> TransportFailure() => new(0, default, null);

    public static ApiResponse<T> Cancelled() => new(-1, default, null);

    public static ApiResponse<T> From(int statusCode, T? value, WireError? error) => new(statusCode, value, error);
}

/// <summary>The minimum surface the kernel needs. Tests replace it wholesale.</summary>
public interface IMosaicApi
{
    Task<ApiResponse<T>> GetAsync<T>(string path, string? accessToken, CancellationToken cancellationToken);

    Task<ApiResponse<T>> PostAsync<T>(string path, object body, string? accessToken, CancellationToken cancellationToken);

    Task<ApiResponse<byte[]>> GetBytesAsync(string path, long maxBytes, string? accessToken, CancellationToken cancellationToken);
}

public interface IMosaicApiFactory
{
    IMosaicApi Create(Uri baseUri);
}