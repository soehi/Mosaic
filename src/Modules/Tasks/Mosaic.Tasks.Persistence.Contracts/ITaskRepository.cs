using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Persistence.Contracts;

public interface ITaskRepository
{
    Task<OperationResult> InsertAsync(TaskItemDto task, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto?>> FindAsync(Guid id, CancellationToken cancellationToken = default);
    // Query order is part of the repository contract; Id formatted as "D" in ordinal order breaks ties.
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default);
    // Counts follow the same filter and archive scope as QueryAsync and ignore Limit/Offset.
    Task<OperationResult<TaskCountsDto>> QueryCountsAsync(TaskQuery query, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryArchiveCandidatesAsync(DateTimeOffset cutoffUtc, int limit, CancellationToken cancellationToken = default);
    Task<OperationResult> ReplaceAsync(TaskItemDto task, long expectedRevision, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    // A receipt and its initial task are committed atomically. Deleting the task preserves its receipt.
    Task<OperationResult<TaskCreateOnceResult>> InsertOnceAsync(
        string sourceId, string requestKey, string payloadFingerprint, TaskItemDto task, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskCreationReceiptDto>>> QueryCreationReceiptsAsync(
        string sourceId, CancellationToken cancellationToken = default);
}
