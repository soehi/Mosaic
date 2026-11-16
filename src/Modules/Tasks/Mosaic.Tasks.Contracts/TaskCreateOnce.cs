using Mosaic.Contracts;

namespace Mosaic.Tasks.Contracts;

// SourceId and RequestKey identify a business request, independent of task title or lifetime.
// IsCompleted is explicit and is never replaced by the user's new-task default.
public sealed record CreateTaskOnceRequest(string SourceId, string RequestKey, string Title, string Notes, bool IsCompleted);
public enum TaskCreateOnceDisposition { Created, AlreadyCreated }
public sealed record TaskCreateOnceResult(Guid TaskId, TaskCreateOnceDisposition Disposition, bool TaskExists);
public sealed record TaskCreationReceiptDto(
    string SourceId, string RequestKey, Guid TaskId, string PayloadFingerprint, DateTimeOffset CreatedAtUtc, bool TaskExists);

public interface ITaskCreateOnceApi
{
    // An existing matching receipt wins before current task-length validation.
    // A different normalized payload for an existing key returns Conflict.
    Task<OperationResult<TaskCreateOnceResult>> CreateOnceAsync(CreateTaskOnceRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskCreationReceiptDto>>> ListReceiptsAsync(string sourceId, CancellationToken cancellationToken = default);
}
