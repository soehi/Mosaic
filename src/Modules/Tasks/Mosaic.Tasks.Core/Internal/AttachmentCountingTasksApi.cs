using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

// 装饰任务 API：读取路径（Get/List）批量附带每个任务的附件数量，写路径在回包前同步计数，
// 保证主列表、速览与浮窗拿到的 DTO 永远携带最新的 AttachmentCount。
// 计数失败不阻断任务操作，回退为 0（与无附件仓储时的行为一致）。
internal sealed class AttachmentCountingTasksApi(ITasksApi inner, ITaskAttachmentRepository attachments) : ITasksApi
{
    public async Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) =>
        await WithCountAsync(inner.CreateAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await WithCountAsync(inner.GetAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var result = await inner.ListAsync(query, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value.Count == 0) return result;
        var counts = await attachments.CountsByTasksAsync(result.Value.Select(task => task.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        if (!counts.IsSuccess) return result;
        return OperationResult<IReadOnlyList<TaskItemDto>>.Success(
            result.Value.Select(task => task with { AttachmentCount = counts.Value.TryGetValue(task.Id, out var count) ? count : 0 }).ToArray());
    }

    public Task<OperationResult<TaskCountsDto>> CountAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
        inner.CountAsync(query, cancellationToken);

    public async Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) =>
        await WithCountAsync(inner.UpdateAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) =>
        await WithCountAsync(inner.SetCompletedAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken cancellationToken = default) =>
        await WithCountAsync(inner.SetArchivedAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);

    public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(request, cancellationToken);

    public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) =>
        inner.GetConstraintsAsync(cancellationToken);

    private async Task<OperationResult<TaskItemDto>> WithCountAsync(Task<OperationResult<TaskItemDto>> operation, CancellationToken cancellationToken)
    {
        var result = await operation.ConfigureAwait(false);
        if (!result.IsSuccess) return result;
        var count = await attachments.CountByTaskAsync(result.Value.Id, cancellationToken).ConfigureAwait(false);
        return count.IsSuccess
            ? OperationResult<TaskItemDto>.Success(result.Value with { AttachmentCount = count.Value })
            : result;
    }
}
