using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskService(
    ITaskRepository repository,
    TimeProvider clock,
    IIdGenerator ids,
    TaskRules rules,
    Action<TaskChange> changed,
    ITaskAttachmentRepository? attachments = null,
    ITaskAttachmentBlobStore? attachmentBlobs = null)
{
    private OperationError? Validate(string title, string notes)
    {
        var constraints = rules.Current;
        if (constraints.TitleRequired && title.Trim().Length == 0)
            return new(ErrorCodes.ValidationFailed, "请输入任务标题。", "Title", MessageKey: "Tasks.Error.TitleRequired");
        if (title.Trim().EnumerateRunes().Count() > constraints.TitleMaxCharacters)
            return new(ErrorCodes.ValidationFailed, $"标题最多 {constraints.TitleMaxCharacters} 个字符。", "Title",
                MessageKey: "Tasks.Error.TitleTooLong");
        if (notes.EnumerateRunes().Count() > constraints.NotesMaxCharacters)
            return new(ErrorCodes.ValidationFailed, $"备注最多 {constraints.NotesMaxCharacters:N0} 个字符。", "Notes",
                MessageKey: "Tasks.Error.NotesTooLong");
        return null;
    }
    internal async Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken ct)
    {
        if (Validate(request.Title, request.Notes) is { } error) return OperationResult<TaskItemDto>.Failure(error);
        var now = clock.GetUtcNow();
        var constraints = rules.Current;
        var complete = request.IsCompleted || constraints.DefaultCompleted;
        var task = new TaskItemDto(ids.NewId(), request.Title.Trim(), request.Notes, complete, now, now, constraints.RevisionStart)
        { CompletedAtUtc = complete ? now : null };
        var saved = await repository.InsertAsync(task, ct).ConfigureAwait(false);
        if(saved.IsSuccess) changed(new(task.Id,task.Revision,TaskChangeKind.Created));
        return saved.IsSuccess ? OperationResult<TaskItemDto>.Success(task) : OperationResult<TaskItemDto>.Failure(saved.Error!);
    }
    internal async Task<OperationResult<TaskCreateOnceResult>> CreateOnceAsync(CreateTaskOnceRequest request, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Title = request.Title.Trim(),
            request.Notes,
            request.IsCompleted
        })));
        // Successful historical requests remain recognizable after the user lowers task limits.
        var receipts = await repository.QueryCreationReceiptsAsync(request.SourceId, ct).ConfigureAwait(false);
        if (!receipts.IsSuccess) return OperationResult<TaskCreateOnceResult>.Failure(receipts.Error!);
        if (receipts.Value.FirstOrDefault(receipt => receipt.RequestKey == request.RequestKey) is { } existing)
            return existing.PayloadFingerprint == fingerprint
                ? OperationResult<TaskCreateOnceResult>.Success(new(existing.TaskId, TaskCreateOnceDisposition.AlreadyCreated, existing.TaskExists))
                : OperationResult<TaskCreateOnceResult>.Failure(CreateOnceConflict());
        if (Validate(request.Title, request.Notes) is { } error) return OperationResult<TaskCreateOnceResult>.Failure(error);
        var now = clock.GetUtcNow();
        var task = new TaskItemDto(ids.NewId(), request.Title.Trim(), request.Notes, request.IsCompleted, now, now, rules.Current.RevisionStart)
        { CompletedAtUtc = request.IsCompleted ? now : null };
        var once = await repository.InsertOnceAsync(request.SourceId, request.RequestKey, fingerprint, task, ct).ConfigureAwait(false);
        if(once.IsSuccess && once.Value.Disposition==TaskCreateOnceDisposition.Created) changed(new(task.Id,task.Revision,TaskChangeKind.Created));
        return once;
    }
    internal Task<OperationResult<IReadOnlyList<TaskCreationReceiptDto>>> ListReceiptsAsync(string sourceId, CancellationToken ct) =>
        repository.QueryCreationReceiptsAsync(sourceId, ct);
    private static OperationError CreateOnceConflict() => new(
        ErrorCodes.Conflict, "此创建请求标识已用于不同的任务内容。", "RequestKey");
    internal async Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var found = await repository.FindAsync(id, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<TaskItemDto>.Failure(found.Error!);
        return found.Value is { } task
            ? OperationResult<TaskItemDto>.Success(task)
            : OperationResult<TaskItemDto>.Failure(new(
                ErrorCodes.NotFound, "任务不存在，可能已被删除。", MessageKey: "Tasks.Error.NotFound"));
    }
    internal async Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken ct)
    {
        var result = await repository.QueryAsync(query, ct).ConfigureAwait(false);
        return result;
    }
    internal Task<OperationResult<TaskCountsDto>> CountAsync(TaskQuery query, CancellationToken ct) =>
        repository.QueryCountsAsync(query, ct);
    internal Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OperationResult<TaskConstraintsDto>.Success(rules.Current));
    }
    internal async Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken ct)
    {
        if (Validate(request.Title, request.Notes) is { } error) return OperationResult<TaskItemDto>.Failure(error);
        return await ChangeAsync(request.Id, request.ExpectedRevision, (task, _) => task with { Title = request.Title.Trim(), Notes = request.Notes }, ct).ConfigureAwait(false);
    }
    internal Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken ct) =>
        ChangeAsync(request.Id, request.ExpectedRevision, (task, now) => task.IsCompleted == request.IsCompleted ? task : task with
        {
            IsCompleted = request.IsCompleted,
            CompletedAtUtc = request.IsCompleted ? now : null,
            RestoredAtUtc = null
        }, ct);

    internal Task<OperationResult<TaskItemDto>> SetArchivedAsync(SetTaskArchivedRequest request, CancellationToken ct) =>
        ChangeAsync(request.Id, request.ExpectedRevision, (task, now) => task.IsArchived == request.IsArchived ? task : task with
        {
            ArchivedAtUtc = request.IsArchived ? now : null,
            RestoredAtUtc = request.IsArchived ? task.RestoredAtUtc : now
        }, ct, allowArchived: true, skipUnchanged: true);

    private async Task<OperationResult<TaskItemDto>> ChangeAsync(Guid id, long revision,
        Func<TaskItemDto, DateTimeOffset, TaskItemDto> change, CancellationToken ct, bool allowArchived = false, bool skipUnchanged = false)
    {
        var found = await repository.FindAsync(id, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<TaskItemDto>.Failure(found.Error!);
        if (found.Value is not { } current || current.Revision != revision) return OperationResult<TaskItemDto>.Failure(Conflict());
        if (current.IsArchived && !allowArchived)
            return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.Conflict, "请先恢复已归档的任务。", MessageKey: "Tasks.Error.Archived"));
        var now = clock.GetUtcNow();
        var next = change(current, now);
        if (skipUnchanged && next == current) return OperationResult<TaskItemDto>.Success(current);
        var updated = next with { Revision = checked(current.Revision + 1), UpdatedAtUtc = now };
        var saved = await repository.ReplaceAsync(updated, revision, ct).ConfigureAwait(false);
        if(saved.IsSuccess) changed(new(updated.Id,updated.Revision,TaskChangeKind.Updated));
        return saved.IsSuccess ? OperationResult<TaskItemDto>.Success(updated) : OperationResult<TaskItemDto>.Failure(saved.Error!);
    }
    internal async Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken ct)
    {
        // 005 迁移后 task_attachments 带 ON DELETE CASCADE：行随任务删除级联消失，
        // 路径信息也随之丢失，因此必须在删除前取出文件路径，删除成功后清理磁盘文件。
        List<(TaskAttachmentDto Attachment, string? StoredPath)>? orphanedFiles = null;
        if (attachments is not null)
        {
            var listed = await attachments.ListByTaskAsync(request.Id, ct).ConfigureAwait(false);
            if (listed.IsSuccess)
            {
                orphanedFiles = [];
                foreach (var attachment in listed.Value)
                {
                    var path = await attachments.FindStoredPathAsync(attachment.Id, ct).ConfigureAwait(false);
                    orphanedFiles.Add((attachment, path.IsSuccess ? path.Value : null));
                }
            }
        }
        var result = await repository.DeleteAsync(request.Id, request.ExpectedRevision, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            if (orphanedFiles is not null)
                foreach (var (attachment, storedPath) in orphanedFiles)
                {
                    if (attachmentBlobs is not null)
                        await attachmentBlobs.DeleteAsync(request.Id, attachment.Id, attachment.FileName, storedPath, ct).ConfigureAwait(false);
                    // SQLite 由 005 的 ON DELETE CASCADE 删行；显式删除保证其他仓储实现语义一致（幂等）。
                    if (attachments is not null)
                        await attachments.DeleteAsync(attachment.Id, ct).ConfigureAwait(false);
                }
            changed(new(request.Id, request.ExpectedRevision + 1, TaskChangeKind.Deleted));
        }
        return result;
    }
    private static OperationError Conflict() => new(
        ErrorCodes.Conflict, "任务已更改或删除。请重新加载后再保存。", MessageKey: "Tasks.Error.Conflict");

    internal async Task<OperationResult<TaskItemDto>> FindForAttachmentAsync(Guid id, CancellationToken ct)
    {
        var found = await repository.FindAsync(id, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<TaskItemDto>.Failure(found.Error!);
        if (found.Value is not { } current)
            return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.NotFound, "任务不存在，可能已被删除。", MessageKey: "Tasks.Error.NotFound"));
        if (current.IsArchived)
            return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.Conflict, "请先恢复已归档的任务。", MessageKey: "Tasks.Error.Archived"));
        return OperationResult<TaskItemDto>.Success(current);
    }

    internal async Task<OperationResult<TaskItemDto>> TouchForAttachmentAsync(TaskItemDto current, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var updated = current with { Revision = checked(current.Revision + 1), UpdatedAtUtc = now };
        var saved = await repository.ReplaceAsync(updated, current.Revision, ct).ConfigureAwait(false);
        if (saved.IsSuccess) return OperationResult<TaskItemDto>.Success(updated);
        if (saved.Error!.Code == ErrorCodes.Conflict) return OperationResult<TaskItemDto>.Failure(Conflict());
        return OperationResult<TaskItemDto>.Failure(saved.Error);
    }
}
