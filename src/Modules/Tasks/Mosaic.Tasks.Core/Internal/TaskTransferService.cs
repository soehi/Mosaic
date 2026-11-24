using System.Security.Cryptography;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

// Export deliberately reuses the public listing path so a backup observes exactly the same
// ordering and archive semantics as the task pages. Import validates every row before the
// repository replaces the table in one transaction. Attachment members are only reachable
// through the explicit backup/restore commands; nothing here polls or watches anything.
internal sealed class TaskTransferService(
    ITasksApi api,
    ITaskTransferRepository repository,
    TaskRules rules,
    Action<TaskChange> changed,
    ITaskAttachmentRepository? attachments,
    ITaskAttachmentBlobStore? blobs)
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

    internal async Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ExportAttachmentsAsync(CancellationToken cancellationToken)
    {
        if (attachments is null)
            return OperationResult<IReadOnlyList<TaskAttachmentDto>>.Success(Array.Empty<TaskAttachmentDto>());
        return await attachments.ListAllAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OperationResult<TaskAttachmentExportDto>> OpenAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        if (attachments is null || blobs is null) return MissingAttachment();
        var found = await attachments.FindAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<TaskAttachmentExportDto>.Failure(found.Error!);
        if (found.Value is not { } attachment) return MissingAttachment();
        var stored = await attachments.FindStoredPathAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess) return OperationResult<TaskAttachmentExportDto>.Failure(stored.Error!);

        // Hash on one open, then reopen for the caller: the returned stream stays at position 0
        // and no caller assumption about seekability is needed.
        string sha256;
        var hashed = await blobs.OpenReadAsync(attachment.TaskId, attachment.Id, attachment.FileName, stored.Value, cancellationToken).ConfigureAwait(false);
        if (!hashed.IsSuccess) return OperationResult<TaskAttachmentExportDto>.Failure(hashed.Error!);
        await using (var content = hashed.Value)
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        var opened = await blobs.OpenReadAsync(attachment.TaskId, attachment.Id, attachment.FileName, stored.Value, cancellationToken).ConfigureAwait(false);
        return opened.IsSuccess
            ? OperationResult<TaskAttachmentExportDto>.Success(new(attachment, opened.Value, sha256))
            : OperationResult<TaskAttachmentExportDto>.Failure(opened.Error!);
    }

    internal async Task<OperationResult> ImportAttachmentAsync(TaskAttachmentDto attachment, string sourcePath, CancellationToken cancellationToken)
    {
        if (attachments is null || blobs is null) return StoreUnavailable();
        if (attachment is null || attachment.Id == Guid.Empty || attachment.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(attachment.FileName))
            return OperationResult.Failure(new(ErrorCodes.ValidationFailed, "附件元数据无效。", MessageKey: "Tasks.Attachments.Error.ImportFailed"));
        if (!File.Exists(sourcePath))
            return OperationResult.Failure(new(ErrorCodes.NotFound, "附件临时文件不存在。", MessageKey: "Tasks.Attachments.Error.ContentMissing"));

        var existing = await attachments.FindAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess) return OperationResult.Failure(existing.Error!);
        var row = existing.Value;
        if (row is not null && row.TaskId != attachment.TaskId)
            return OperationResult.Failure(new(ErrorCodes.Conflict, "附件标识已被其他任务占用。", MessageKey: "Tasks.Attachments.Error.ImportFailed"));

        var imported = await blobs.ImportAsync(attachment.TaskId, attachment.Id, attachment.FileName, sourcePath, cancellationToken).ConfigureAwait(false);
        if (!imported.IsSuccess) return OperationResult.Failure(imported.Error!);
        if (row is not null) return OperationResult.Success(); // 行已存在（任务未被替换掉）：只覆盖内容，不动元数据与 revision

        var inserted = await attachments.InsertAsync(attachment, imported.Value, cancellationToken).ConfigureAwait(false);
        if (inserted.IsSuccess) return inserted;
        // 元数据写入失败时把刚导入的文件删掉，避免留下"无行引用"的副本。
        await blobs.DeleteAsync(attachment.TaskId, attachment.Id, attachment.FileName, imported.Value, cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    internal async Task<OperationResult> DeleteAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        if (attachments is null || blobs is null) return StoreUnavailable();
        var found = await attachments.FindAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult.Failure(found.Error!);
        if (found.Value is not { } attachment) return OperationResult.Success();
        var stored = await attachments.FindStoredPathAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        var deleted = await attachments.DeleteAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (!deleted.IsSuccess) return deleted;
        await blobs.DeleteAsync(attachment.TaskId, attachment.Id, attachment.FileName, stored.IsSuccess ? stored.Value : null, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success();
    }

    internal async Task<OperationResult<int>> RemoveAttachmentsNotInAsync(IReadOnlyCollection<Guid> keepIds, CancellationToken cancellationToken)
    {
        if (attachments is null || blobs is null) return OperationResult<int>.Success(0);
        if (keepIds is null) return OperationResult<int>.Failure(new(ErrorCodes.ValidationFailed, "附件清单无效。"));
        var keep = new HashSet<Guid>(keepIds);
        var all = await attachments.ListAllAsync(cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess) return OperationResult<int>.Failure(all.Error!);
        var removed = 0;
        foreach (var attachment in all.Value)
        {
            if (keep.Contains(attachment.Id)) continue;
            var result = await DeleteAttachmentAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return OperationResult<int>.Failure(result.Error!);
            removed++;
        }
        return OperationResult<int>.Success(removed);
    }

    internal async Task<OperationResult<int>> SweepOrphanAttachmentsAsync(CancellationToken cancellationToken)
    {
        if (attachments is null || blobs is null) return OperationResult<int>.Success(0);
        var all = await attachments.ListAllAsync(cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess) return OperationResult<int>.Failure(all.Error!);
        var keep = all.Value.Select(attachment => attachment.Id).ToHashSet();
        return await blobs.SweepAsync(keep, cancellationToken).ConfigureAwait(false);
    }

    private static OperationResult<TaskAttachmentExportDto> MissingAttachment() =>
        OperationResult<TaskAttachmentExportDto>.Failure(new(ErrorCodes.NotFound, "附件不存在。", MessageKey: "Tasks.Attachments.Error.NotFound"));

    private static OperationResult StoreUnavailable() =>
        OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "附件存储不可用。", MessageKey: "Tasks.Attachments.Error.StoreUnavailable"));

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