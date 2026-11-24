using Mosaic.Contracts;

namespace Mosaic.Tasks.Contracts;

public sealed record TaskItemDto(Guid Id, string Title, string Notes, bool IsCompleted, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Revision)
{
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
    public DateTimeOffset? RestoredAtUtc { get; init; }
    public bool IsArchived => ArchivedAtUtc.HasValue;
    public long AttachmentCount { get; init; }
    public bool HasAttachments => AttachmentCount > 0;
}
public enum TaskFilter { All, Open, Completed }
public enum TaskSortOrder { CreatedDesc, CreatedAsc, TitleAsc, TitleDesc }
public enum TaskArchiveScope { Active, Archived, All }
public sealed record TaskQuery(TaskFilter Filter = TaskFilter.All, TaskSortOrder SortOrder = TaskSortOrder.CreatedDesc)
{
    public TaskArchiveScope ArchiveScope { get; init; }
    /// <summary>最多返回多少行；0 表示不限制。预览列表只按第一页取数，计数用 <see cref="ITasksApi.CountAsync"/>。</summary>
    public int Limit { get; init; }
    /// <summary>跳过的行数，仅在 <see cref="Limit"/> 大于 0 时生效。</summary>
    public int Offset { get; init; }
    /// <summary>列表行只显示标题与状态；备注由编辑器和详情通过 GetAsync 读取。默认保留备注以免影响导出。</summary>
    public bool IncludeNotes { get; init; } = true;
}

/// <summary>与筛选条件一致的聚合计数：总行数与未完成行数。</summary>
public sealed record TaskCountsDto(int Total, int Open);
public sealed record TaskConstraintsDto(int TitleMaxCharacters, int NotesMaxCharacters, bool TitleRequired, bool DefaultCompleted, long RevisionStart,
    int AttachmentMaxFileMegabytes, int AttachmentMaxPerTask);
public sealed record CreateTaskRequest(string Title, string Notes, bool IsCompleted = false);
public sealed record UpdateTaskRequest(Guid Id, string Title, string Notes, long ExpectedRevision);
public sealed record SetTaskCompletedRequest(Guid Id, bool IsCompleted, long ExpectedRevision);
public sealed record SetTaskArchivedRequest(Guid Id, bool IsArchived, long ExpectedRevision);
public sealed record DeleteTaskRequest(Guid Id, long ExpectedRevision);

public interface ITasksApi
{
    Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default);
    /// <summary>按同一筛选与归档范围计数，不受分页限制。</summary>
    Task<OperationResult<TaskCountsDto>> CountAsync(TaskQuery query, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default);
}
