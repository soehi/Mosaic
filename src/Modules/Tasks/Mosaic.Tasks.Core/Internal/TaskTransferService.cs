using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

// Export deliberately reuses the public listing path so a backup observes exactly the same
// ordering and archive semantics as the task pages. Import validates every row before the
// repository replaces the table in one transaction.
internal sealed class TaskTransferService(ITasksApi api, ITaskTransferRepository repository, TaskRules rules, Action<TaskChange> changed)
{
    internal const int MaxTransferItems = 100_000;

    internal async Task<OperationResult<IReadOnlyList<TaskItemDto>>> ExportAllAsync(CancellationToken cancellationToken)
    {
        var listed = await api.ListAsync(new TaskQuery(TaskFilter.All, TaskSortOrder.CreatedAsc) { ArchiveScope = TaskArchiveScope.All }, cancellationToken).ConfigureAwait(false);
        return listed;
    }

    internal async Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken)
    {
        if (tasks is null) return OperationResult.Failure(new(ErrorCodes.ValidationFailed, "备份内容无效。"));
        if (tasks.Count > MaxTransferItems)
            return OperationResult.Failure(new(ErrorCodes.ValidationFailed, $"一次最多恢复 {MaxTransferItems:N0} 个任务。"));
        var ids = new HashSet<Guid>();
        foreach (var task in tasks)
        {
            var error = Validate(task, ids);
            if (error is not null) return OperationResult.Failure(error);
        }
        var replaced = await repository.ReplaceAllAsync(tasks, cancellationToken).ConfigureAwait(false);
        if (!replaced.IsSuccess) return replaced;
        // A restore replaces the whole table at once; observers get one bulk notification instead of
        // one per row, which used to turn a 100k-task restore into 100k list reloads.
        changed(new(Guid.Empty, 0, TaskChangeKind.Replaced));
        return OperationResult.Success();
    }

    private OperationError? Validate(TaskItemDto? task, HashSet<Guid> ids)
    {
        if (task is null || task.Title is null || task.Notes is null)
            return new(ErrorCodes.ValidationFailed, "备份中的任务内容无效。");
        if (task.Id == Guid.Empty || !ids.Add(task.Id))
            return new(ErrorCodes.ValidationFailed, "备份中的任务标识无效或重复。");
        if (task.Revision < 1)
            return new(ErrorCodes.ValidationFailed, "备份中的任务版本无效。");
        if (task.CreatedAtUtc == default || task.UpdatedAtUtc == default || task.UpdatedAtUtc < task.CreatedAtUtc)
            return new(ErrorCodes.ValidationFailed, "备份中的任务时间无效。");
        var constraints = rules.Current;
        if (task.Title.Trim().EnumerateRunes().Count() > constraints.TitleMaxCharacters)
            return new(ErrorCodes.ValidationFailed, $"备份中的任务标题超过 {constraints.TitleMaxCharacters} 个字符。");
        if (task.Notes.EnumerateRunes().Count() > constraints.NotesMaxCharacters)
            return new(ErrorCodes.ValidationFailed, $"备份中的任务备注超过 {constraints.NotesMaxCharacters:N0} 个字符。");
        return null;
    }
}
