using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Auth;

/// <summary>Outcome of one HTTP exchange. Never carries secret response headers.</summary>
public sealed record ApiResult<T>
{
    public T? Value { get; init; }

    public int StatusCode { get; init; }

    public WireErrorDto? Error { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 300;

    public bool IsUnauthorized => StatusCode == 401;

    public string? ErrorCode => Error?.Code;

    public static ApiResult<T> Ok(T value, int statusCode = 200) =>
        new() { Value = value, StatusCode = statusCode };

    public static ApiResult<T> Fail(int statusCode, WireErrorDto? error, TimeSpan? retryAfter = null) =>
        new() { StatusCode = statusCode, Error = error, RetryAfter = retryAfter };
}

/// <summary>Outcome of a command that returns no useful body.</summary>
public sealed record ApiResult
{
    public int StatusCode { get; init; }

    public WireErrorDto? Error { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 300;

    public bool IsUnauthorized => StatusCode == 401;

    public string? ErrorCode => Error?.Code;

    public static ApiResult Ok(int statusCode = 204) => new() { StatusCode = statusCode };

    public static ApiResult Fail(int statusCode, WireErrorDto? error, TimeSpan? retryAfter = null) =>
        new() { StatusCode = statusCode, Error = error, RetryAfter = retryAfter };
}

/// <summary>Code used for failures that never reached the server.</summary>
public static class LocalTransportCodes
{
    public const string Network = "network.error";

    public const string Timeout = "network.timeout";

    public const string Protocol = "client.protocol";

    public const string Cancelled = "client.cancelled";
}
