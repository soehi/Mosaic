using Mosaic.Contracts;

namespace Mosaic.Tasks.Contracts;

public static class TaskAttachmentConstraints
{
    public const int MaxFileMegabytes = 50;
    public const int MaxPerTask = 10;
    public const int FileNameMaxCharacters = 260;
}

public enum TaskAttachmentTransferMode { Copy, Move }

public sealed record TaskAttachmentDto(
    Guid Id,
    Guid TaskId,
    string FileName,
    long SizeBytes,
    DateTimeOffset AddedAtUtc)
{
    public string StoredPath { get; init; } = "";
}

public sealed record AddTaskAttachmentRequest(Guid TaskId, string SourcePath, long ExpectedRevision);
public sealed record ExportTaskAttachmentRequest(
    Guid TaskId,
    Guid AttachmentId,
    string TargetDirectory,
    TaskAttachmentTransferMode Mode,
    long ExpectedRevision);
public sealed record RemoveTaskAttachmentRequest(Guid TaskId, Guid AttachmentId, long ExpectedRevision);

public interface ITaskAttachmentApi
{
    Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> AddAsync(AddTaskAttachmentRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> ExportAsync(ExportTaskAttachmentRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> RemoveAsync(RemoveTaskAttachmentRequest request, CancellationToken cancellationToken = default);
}
