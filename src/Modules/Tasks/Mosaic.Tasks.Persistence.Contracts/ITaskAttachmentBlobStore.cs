using Mosaic.Contracts;

namespace Mosaic.Tasks.Persistence.Contracts;

/// <summary>
/// 附件二进制内容的唯一所有者。路径解析、根目录约束与文件系统访问都在实现内部完成，
/// 上层只按 (taskId, attachmentId, fileName) 取用，不再接触绝对路径。
/// </summary>
public interface ITaskAttachmentBlobStore
{
    /// <summary>当前是否有可用的附件根目录（未配置时为 false，新增附件直接拒绝）。</summary>
    bool CanStore { get; }

    /// <summary>保存副本并返回持久化路径（仅用于写入仓储，不进入任何公开契约）。目标已存在即失败。</summary>
    Task<OperationResult<string>> SaveAsync(Guid taskId, Guid attachmentId, string fileName, string sourcePath,
        CancellationToken cancellationToken = default);

    /// <summary>打开只读流；内容缺失返回 NotFound（MessageKey = Tasks.Attachments.Error.ContentMissing）。</summary>
    Task<OperationResult<Stream>> OpenReadAsync(Guid taskId, Guid attachmentId, string fileName, string? storedPathHint, CancellationToken cancellationToken = default);

    /// <summary>删除内容；文件不存在视为成功（幂等），根目录之外的路径一律忽略。</summary>
    Task<OperationResult> DeleteAsync(Guid taskId, Guid attachmentId, string fileName, string? storedPathHint, CancellationToken cancellationToken = default);

    /// <summary>判断路径是否位于受管附件根目录内（导出目标目录校验用）。</summary>
    bool IsWithinStore(string path);
}