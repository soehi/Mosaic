namespace Mosaic.Server.Http;

public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string message, object? details = null, int? retryAfterSeconds = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int Status { get; }

    public string Code { get; }

    public object? Details { get; }

    public int? RetryAfterSeconds { get; }
}
