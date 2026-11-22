using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskAttachmentService(
    TaskService tasks,
    ITaskAttachmentRepository repository,
    ITaskAttachmentBlobStore blobs,
    TaskRules rules,
    Action<TaskChange> changed,
    TimeProvider clock,
    IIdGenerator ids)
{
    internal Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListAsync(Guid taskId, CancellationToken ct) =>
        repository.ListByTaskAsync(taskId, ct);

    internal async Task<OperationResult<Stream>> OpenReadAsync(Guid taskId, Guid attachmentId, CancellationToken ct)
    {
        var found = await tasks.FindForAttachmentAsync(taskId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return OperationResult<Stream>.Failure(found.Error!);
        var attachment = await FindAsync(found.Value, attachmentId, ct).ConfigureAwait(false);
        if (!attachment.IsSuccess) return OperationResult<Stream>.Failure(attachment.Error!);
        var path = await repository.FindStoredPathAsync(attachmentId, ct).ConfigureAwait(false);
        if (!path.IsSuccess) return OperationResult<Stream>.Failure(path.Error!);
        return await blobs.OpenReadAsync(taskId, attachmentId, attachment.Value.FileName, path.Value, ct).ConfigureAwait(false);
    }

    internal async Task<OperationResult<TaskItemDto>> AddAsync(AddTaskAttachmentRequest request, CancellationToken ct)
    {
        if (!blobs.CanStore) return OperationResult<TaskItemDto>.Failure(StoreUnavailable());
        var found = await tasks.FindForAttachmentAsync(request.TaskId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return found;
        var task = found.Value;
        if (task.Revision != request.ExpectedRevision) return OperationResult<TaskItemDto>.Failure(Conflict());

        if (!File.Exists(request.SourcePath))
            return OperationResult<TaskItemDto>.Failure(Invalid("找不到要添加的文件。", "SourcePath"));
        var fileName = Path.GetFileName(request.SourcePath);
        if (fileName.Length == 0 || fileName.EnumerateRunes().Count() > TaskAttachmentConstraints.FileNameMaxCharacters ||
            fileName.Any(char.IsControl) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return OperationResult<TaskItemDto>.Failure(Invalid("文件名无效。", "SourcePath"));
        long size;
        try { size = new FileInfo(request.SourcePath).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult<TaskItemDto>.Failure(Invalid("无法读取要添加的文件。", "SourcePath"));
        }
        var constraints = rules.Current;
        if (size > constraints.AttachmentMaxFileMegabytes * 1024L * 1024L)
            return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.ValidationFailed,
                $"附件超过 {constraints.AttachmentMaxFileMegabytes} MB 大小限制。", "SourcePath", MessageKey: "Tasks.Attachments.Error.TooLarge"));
        var count = await repository.CountByTaskAsync(task.Id, ct).ConfigureAwait(false);
        if (!count.IsSuccess) return OperationResult<TaskItemDto>.Failure(count.Error!);
        if (count.Value >= constraints.AttachmentMaxPerTask)
            return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.ValidationFailed,
                $"每个任务最多 {constraints.AttachmentMaxPerTask} 个附件。", "SourcePath", MessageKey: "Tasks.Attachments.Error.TooMany"));

        var id = ids.NewId();
        var saved = await blobs.SaveAsync(task.Id, id, fileName, request.SourcePath, ct).ConfigureAwait(false);
        if (!saved.IsSuccess) return OperationResult<TaskItemDto>.Failure(saved.Error!);
        var dto = new TaskAttachmentDto(id, task.Id, fileName, size, clock.GetUtcNow());
        var inserted = await repository.InsertAsync(dto, saved.Value, ct).ConfigureAwait(false);
        if (!inserted.IsSuccess)
        {
            await blobs.DeleteAsync(task.Id, id, fileName, saved.Value, ct).ConfigureAwait(false);
            return OperationResult<TaskItemDto>.Failure(inserted.Error!);
        }
        return await TouchAsync(task, ct).ConfigureAwait(false);
    }

    internal async Task<OperationResult<TaskItemDto>> ExportAsync(ExportTaskAttachmentRequest request, CancellationToken ct)
    {
        var found = await tasks.FindForAttachmentAsync(request.TaskId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return found;
        var task = found.Value;
        if (task.Revision != request.ExpectedRevision) return OperationResult<TaskItemDto>.Failure(Conflict());

        var attachmentResult = await FindAsync(task, request.AttachmentId, ct).ConfigureAwait(false);
        if (!attachmentResult.IsSuccess) return OperationResult<TaskItemDto>.Failure(attachmentResult.Error!);
        var attachment = attachmentResult.Value;

        if (string.IsNullOrWhiteSpace(request.TargetDirectory) || !Directory.Exists(request.TargetDirectory))
            return OperationResult<TaskItemDto>.Failure(Invalid("目标文件夹不是有效目录。", "TargetDirectory"));
        string targetDirectory;
        try { targetDirectory = Path.GetFullPath(request.TargetDirectory); }
        catch { return OperationResult<TaskItemDto>.Failure(Invalid("目标文件夹不是有效目录。", "TargetDirectory")); }
        if (blobs.IsWithinStore(targetDirectory))
            return OperationResult<TaskItemDto>.Failure(Invalid("不能导出到应用管理的附件目录。", "TargetDirectory"));

        var destination = Path.Combine(targetDirectory, attachment.FileName);
        if (File.Exists(destination))
            return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.Conflict, "目标文件夹已存在同名文件。", "TargetDirectory", MessageKey: "Tasks.Attachments.Error.TargetExists"));

        var path = await repository.FindStoredPathAsync(attachment.Id, ct).ConfigureAwait(false);
        if (!path.IsSuccess) return OperationResult<TaskItemDto>.Failure(path.Error!);
        var opened = await blobs.OpenReadAsync(task.Id, attachment.Id, attachment.FileName, path.Value, ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return OperationResult<TaskItemDto>.Failure(opened.Error!);

        await using (opened.Value.ConfigureAwait(false))
        {
            try
            {
                await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await opened.Value.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(destination);
                return OperationResult<TaskItemDto>.Failure(new(ErrorCodes.InternalError, "无法写入目标文件夹，请检查权限后重试。", MessageKey: "Tasks.Attachments.Error.WriteFailed"));
            }
        }

        if (request.Mode == TaskAttachmentTransferMode.Move)
        {
            var deleted = await repository.DeleteAsync(attachment.Id, ct).ConfigureAwait(false);
            if (!deleted.IsSuccess)
            {
                TryDelete(destination);
                return OperationResult<TaskItemDto>.Failure(deleted.Error!);
            }
            await blobs.DeleteAsync(task.Id, attachment.Id, attachment.FileName, path.Value, ct).ConfigureAwait(false);
        }
        return await TouchAsync(task, ct).ConfigureAwait(false);
    }

    internal async Task<OperationResult<TaskItemDto>> RemoveAsync(RemoveTaskAttachmentRequest request, CancellationToken ct)
    {
        var found = await tasks.FindForAttachmentAsync(request.TaskId, ct).ConfigureAwait(false);
        if (!found.IsSuccess) return found;
        var task = found.Value;
        if (task.Revision != request.ExpectedRevision) return OperationResult<TaskItemDto>.Failure(Conflict());

        var attachmentResult = await FindAsync(task, request.AttachmentId, ct).ConfigureAwait(false);
        if (!attachmentResult.IsSuccess) return OperationResult<TaskItemDto>.Failure(attachmentResult.Error!);
        var path = await repository.FindStoredPathAsync(request.AttachmentId, ct).ConfigureAwait(false);
        if (!path.IsSuccess) return OperationResult<TaskItemDto>.Failure(path.Error!);
        var deleted = await repository.DeleteAsync(request.AttachmentId, ct).ConfigureAwait(false);
        if (!deleted.IsSuccess) return OperationResult<TaskItemDto>.Failure(deleted.Error!);
        await blobs.DeleteAsync(task.Id, request.AttachmentId, attachmentResult.Value.FileName, path.Value, ct).ConfigureAwait(false);
        return await TouchAsync(task, ct).ConfigureAwait(false);
    }

    private async Task<OperationResult<TaskAttachmentDto>> FindAsync(TaskItemDto task, Guid attachmentId, CancellationToken ct)
    {
        var result = await repository.FindAsync(attachmentId, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return OperationResult<TaskAttachmentDto>.Failure(result.Error!);
        return result.Value is { } attachment && attachment.TaskId == task.Id
            ? OperationResult<TaskAttachmentDto>.Success(attachment)
            : OperationResult<TaskAttachmentDto>.Failure(new(ErrorCodes.NotFound, "附件不存在。", MessageKey: "Tasks.Attachments.Error.NotFound"));
    }

    private async Task<OperationResult<TaskItemDto>> TouchAsync(TaskItemDto task, CancellationToken ct)
    {
        var touched = await tasks.TouchForAttachmentAsync(task, ct).ConfigureAwait(false);
        if (touched.IsSuccess) changed(new(task.Id, touched.Value.Revision, TaskChangeKind.Updated));
        return touched;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }
    private static OperationError Invalid(string message, string field) => new(ErrorCodes.ValidationFailed, message, field);
    private static OperationError StoreUnavailable() => new(ErrorCodes.CapabilityUnavailable, "附件存储不可用，请重启应用后重试。", MessageKey: "Tasks.Attachments.Error.StoreUnavailable");
    private static OperationError Conflict() => new(ErrorCodes.Conflict, "任务已更改或删除。请重新加载后再保存。", MessageKey: "Tasks.Error.Conflict");
}