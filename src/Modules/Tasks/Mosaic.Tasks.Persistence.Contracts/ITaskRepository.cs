using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Persistence.Contracts;

public interface ITaskRepository
{
    Task<OperationResult> InsertAsync(TaskItemDto task, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto?>> FindAsync(Guid id, CancellationToken cancellationToken = default);
    // Query order is part of the repository contract: newest CreatedAtUtc first, then Id formatted as "D" in ordinal order.
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default);
    Task<OperationResult> ReplaceAsync(TaskItemDto task, long expectedRevision, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
}
