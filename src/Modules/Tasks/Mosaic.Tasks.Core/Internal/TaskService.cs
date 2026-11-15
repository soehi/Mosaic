using System.Text;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskService(ITaskRepository repository, TimeProvider clock, IIdGenerator ids, TaskRules rules)
{
    private OperationError? Validate(string title, string notes)
    {
        var constraints = rules.Current;
        if (constraints.TitleRequired && title.Trim().Length == 0) return new(ErrorCodes.ValidationFailed, "请输入任务标题。", "Title");
        if (title.Trim().EnumerateRunes().Count() > constraints.TitleMaxCharacters)
            return new(ErrorCodes.ValidationFailed, $"标题最多 {constraints.TitleMaxCharacters} 个字符。", "Title");
        if (notes.EnumerateRunes().Count() > constraints.NotesMaxCharacters)
            return new(ErrorCodes.ValidationFailed, $"备注最多 {constraints.NotesMaxCharacters:N0} 个字符。", "Notes");
        return null;
    }
    internal async Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken ct)
    {
        if (Validate(request.Title, request.Notes) is { } error) return OperationResult<TaskItemDto>.Failure(error);
        var now = clock.GetUtcNow();
        var constraints = rules.Current;
        var task = new TaskItemDto(ids.NewId(), request.Title.Trim(), request.Notes, request.IsCompleted || constraints.DefaultCompleted, now, now, constraints.RevisionStart);
        var saved = await repository.InsertAsync(task, ct).ConfigureAwait(false);
        return saved.IsSuccess ? OperationResult<TaskItemDto>.Success(task) : OperationResult<TaskItemDto>.Failure(saved.Error!);
    }
    internal async Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var found = await repository.FindAsync(id, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<TaskItemDto>.Failure(found.Error!);
        return found.Value is { } task ? OperationResult<TaskItemDto>.Success(task) : OperationResult<TaskItemDto>.Failure(new(ErrorCodes.NotFound, "任务不存在，可能已被删除。"));
    }
    internal async Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken ct)
    {
        var result = await repository.QueryAsync(query, ct).ConfigureAwait(false);
        return result;
    }
    internal Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OperationResult<TaskConstraintsDto>.Success(rules.Current));
    }
    internal async Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken ct)
    {
        if (Validate(request.Title, request.Notes) is { } error) return OperationResult<TaskItemDto>.Failure(error);
        return await ChangeAsync(request.Id, request.ExpectedRevision, t => t with { Title = request.Title.Trim(), Notes = request.Notes }, ct).ConfigureAwait(false);
    }
    internal Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken ct) =>
        ChangeAsync(request.Id, request.ExpectedRevision, t => t with { IsCompleted = request.IsCompleted }, ct);

    private async Task<OperationResult<TaskItemDto>> ChangeAsync(Guid id, long revision, Func<TaskItemDto, TaskItemDto> change, CancellationToken ct)
    {
        var found = await repository.FindAsync(id, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<TaskItemDto>.Failure(found.Error!);
        if (found.Value is not { } current || current.Revision != revision) return OperationResult<TaskItemDto>.Failure(Conflict());
        var updated = change(current) with { Revision = checked(current.Revision + 1), UpdatedAtUtc = clock.GetUtcNow() };
        var saved = await repository.ReplaceAsync(updated, revision, ct).ConfigureAwait(false);
        return saved.IsSuccess ? OperationResult<TaskItemDto>.Success(updated) : OperationResult<TaskItemDto>.Failure(saved.Error!);
    }
    internal Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken ct) => repository.DeleteAsync(request.Id, request.ExpectedRevision, ct);
    private static OperationError Conflict() => new(ErrorCodes.Conflict, "任务已更改或删除。请重新加载后再保存。");
}
