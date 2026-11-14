namespace Mosaic.Contracts;

public sealed record OperationError(string Code, string Message, string? Field = null, string? CorrelationId = null);

public static class ErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "TASK_NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string CapabilityUnavailable = "CAPABILITY_UNAVAILABLE";
    public const string Canceled = "CANCELED";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed class OperationResult
{
    private OperationResult(OperationError? error) => Error = error;
    public bool IsSuccess => Error is null;
    public OperationError? Error { get; }
    public static OperationResult Success() => new(null);
    public static OperationResult Failure(OperationError error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class OperationResult<T>
{
    private readonly T? _value;
    private OperationResult(T? value, OperationError? error) { _value = value; Error = error; }
    public bool IsSuccess => Error is null;
    public OperationError? Error { get; }
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result has no value.");
    public static OperationResult<T> Success(T value) => new(value, null);
    public static OperationResult<T> Failure(OperationError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}
