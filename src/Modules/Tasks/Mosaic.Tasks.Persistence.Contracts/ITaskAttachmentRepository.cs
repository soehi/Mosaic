using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Persistence.Contracts;

public interface ITaskAttachmentRepository
{
    Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListByTaskAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<OperationResult> InsertAsync(TaskAttachmentDto attachment, string storedPath, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskAttachmentDto?>> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default);
    Task<OperationResult<string?>> FindStoredPathAsync(Guid attachmentId, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListExpiredArchivedAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
    Task<OperationResult<long>> CountByTaskAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyDictionary<Guid, long>>> CountsByTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default);
}
