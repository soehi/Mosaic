using Mosaic.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

/// <summary>
/// 附件内容在本地文件系统的唯一实现。root 由 TasksModule 在激活时单点写入（Restart 语义）；
/// 路径解析、root 边界校验、目录创建与文件删除全部封在本类内，上层只看到不透明句柄与流。
/// </summary>
internal sealed class FileSystemTaskAttachmentBlobStore : ITaskAttachmentBlobStore
{
    private readonly object _sync = new();
    private string? _root;

    /// <summary>root 只在锁内读写：Apply 与 CanStore/SaveAsync/Resolve 可能来自设置线程与附件线程。</summary>
    private string? Root { get { lock (_sync) return _root; } }

    /// <summary>由设置值（或构造默认值）决定 root；空值/非法路径把 root 置空。</summary>
    internal void Apply(IReadOnlyDictionary<string, object?> values)
    {
        string? root = null;
        if (values.TryGetValue("tasks.attachments.storagePath", out var value) && value is string configured && configured.Trim().Length > 0)
        {
            try { root = Path.GetFullPath(configured.Trim()); } catch { root = null; }
        }
        lock (_sync) _root = root;
    }

    public bool CanStore => Root is not null;

    public async Task<OperationResult<string>> SaveAsync(Guid taskId, Guid attachmentId, string fileName, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (Root is not { } root)
            return OperationResult<string>.Failure(new(ErrorCodes.CapabilityUnavailable, "附件存储不可用，请重启应用后重试。", MessageKey: "Tasks.Attachments.Error.StoreUnavailable"));
        var storedPath = PathFor(root, taskId, attachmentId, fileName);
        // 源文件已在附件目录内：再拖入只会产生副本，明确拒绝而不是静默复制。
        if (IsWithinStore(sourcePath))
            return OperationResult<string>.Failure(new(ErrorCodes.ValidationFailed, "该文件已经在附件目录中。", "SourcePath", MessageKey: "Tasks.Attachments.Error.AlreadyStored"));

        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var target = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            created = true;
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (created) TryDelete(storedPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 目标已存在时 CreateNew 直接失败，此时 created=false：绝不删除别人的文件。
            if (created) TryDelete(storedPath);
            var existing = !created && File.Exists(storedPath);
            return OperationResult<string>.Failure(existing
                ? new(ErrorCodes.Conflict, "附件目录已有同名文件，请移除后重试。", "SourcePath", MessageKey: "Tasks.Attachments.Error.SaveConflict")
                : new(ErrorCodes.InternalError, "无法保存附件，请重试。", MessageKey: "Tasks.Attachments.Error.SaveFailed"));
        }
        return OperationResult<string>.Success(storedPath);
    }

    public Task<OperationResult<Stream>> OpenReadAsync(Guid taskId, Guid attachmentId, string fileName, string? storedPathHint, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(taskId, attachmentId, fileName, storedPathHint);
        if (resolved is null || !File.Exists(resolved))
            return Task.FromResult(OperationResult<Stream>.Failure(new(ErrorCodes.NotFound, "附件内容已丢失。", MessageKey: "Tasks.Attachments.Error.ContentMissing")));
        try
        {
            Stream stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Task.FromResult(OperationResult<Stream>.Success(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(OperationResult<Stream>.Failure(new(ErrorCodes.InternalError, "无法读取附件内容。", MessageKey: "Tasks.Attachments.Error.ContentMissing")));
        }
    }

    public async Task<OperationResult<string>> ImportAsync(Guid taskId, Guid attachmentId, string fileName, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (Root is not { } root)
            return OperationResult<string>.Failure(new(ErrorCodes.CapabilityUnavailable, "附件存储不可用，请重启应用后重试。", MessageKey: "Tasks.Attachments.Error.StoreUnavailable"));
        var storedPath = PathFor(root, taskId, attachmentId, fileName);
        // 源文件就是受管目标（例如重复恢复）：无需也不允许再写一次，否则 FileMode.Create 会先截断源。
        if (IsSamePath(sourcePath, storedPath))
            return File.Exists(storedPath)
                ? OperationResult<string>.Success(storedPath)
                : OperationResult<string>.Failure(new(ErrorCodes.NotFound, "附件内容已丢失。", MessageKey: "Tasks.Attachments.Error.ContentMissing"));

        var wrote = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var target = new FileStream(storedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            wrote = true;
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (wrote) TryDelete(storedPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 已截断/写了一半的目标留在盘上只会成为不可用的半文件，删除后由下次恢复重试。
            if (wrote) TryDelete(storedPath);
            return OperationResult<string>.Failure(new(ErrorCodes.InternalError, "无法写入附件，请重试。", MessageKey: "Tasks.Attachments.Error.ImportFailed"));
        }
        return OperationResult<string>.Success(storedPath);
    }

    public Task<OperationResult<int>> SweepAsync(IReadOnlyCollection<Guid> keepAttachmentIds, CancellationToken cancellationToken = default)
    {
        if (Root is not { } root || !Directory.Exists(root))
            return Task.FromResult(OperationResult<int>.Success(0));
        var keep = new HashSet<Guid>(keepAttachmentIds);
        var removed = 0;
        try
        {
            foreach (var taskDirectory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var managedTaskDirectory = Guid.TryParseExact(Path.GetFileName(taskDirectory), "D", out _);
                if (!managedTaskDirectory) continue; // 用户自建目录一律不碰
                foreach (var file in Directory.EnumerateFiles(taskDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var attachmentId)
                        || keep.Contains(attachmentId))
                        continue;
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 占用中的文件留到下次清理。
                    }
                }
                if (!Directory.EnumerateFileSystemEntries(taskDirectory).Any())
                {
                    try { Directory.Delete(taskDirectory); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(OperationResult<int>.Failure(new(ErrorCodes.InternalError, "无法清理附件目录。", MessageKey: "Tasks.Attachments.Error.SweepFailed")));
        }
        return Task.FromResult(OperationResult<int>.Success(removed));
    }

    public Task<OperationResult> DeleteAsync(Guid taskId, Guid attachmentId, string fileName, string? storedPathHint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(taskId, attachmentId, fileName, storedPathHint);
        TryDelete(resolved);
        return Task.FromResult(OperationResult.Success());
    }

    public bool IsWithinStore(string path)
    {
        if (Root is not { } root || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            // 用相对路径判定包含关系：目标路径可以还不存在（导出目录），Path.GetFullPath 对其同样有效。
            var relative = Path.GetRelativePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.GetFullPath(path));
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
        }
        catch { return false; }
    }

    /// <summary>
    /// 优先使用仓储里的历史路径（升级前写入的记录），但必须落在受管 root 内；
    /// 无 hint 或越界时按 (taskId, attachmentId, fileName) 重新推导。
    /// </summary>
    private string? Resolve(Guid taskId, Guid attachmentId, string fileName, string? storedPathHint)
    {
        if (Root is not { } root) return null;
        if (!string.IsNullOrWhiteSpace(storedPathHint) && IsWithinStore(storedPathHint)) return Path.GetFullPath(storedPathHint);
        return PathFor(root, taskId, attachmentId, fileName);
    }

    /// <summary>受管布局的唯一写出点：{root}/{taskId}/{attachmentId}{ext}。</summary>
    private static string PathFor(string root, Guid taskId, Guid attachmentId, string fileName) =>
        Path.Combine(root, taskId.ToString("D"), attachmentId.ToString("D") + Path.GetExtension(fileName));

    private static bool IsSamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}