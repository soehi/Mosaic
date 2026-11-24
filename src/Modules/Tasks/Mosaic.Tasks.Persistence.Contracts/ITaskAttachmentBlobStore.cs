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

    /// <summary>
    /// 把外部文件导入受管布局 {root}/{taskId}/{attachmentId}{ext}，覆盖同路径已有内容
    /// （仅用于备份下载恢复：目标路径由 ID 推导，同名即同一附件）。返回持久化路径。
    /// </summary>
    Task<OperationResult<string>> ImportAsync(Guid taskId, Guid attachmentId, string fileName, string sourcePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除不在 <paramref name="keepAttachmentIds"/> 中的受管附件文件：只认
    /// "Guid 目录 / Guid 文件名" 布局，绝不碰用户手动放进附件目录的其他文件。返回删除的文件数。
    /// </summary>
    Task<OperationResult<int>> SweepAsync(IReadOnlyCollection<Guid> keepAttachmentIds,
        CancellationToken cancellationToken = default);

    /// <summary>判断路径是否位于受管附件根目录内（导出目标目录校验用）。</summary>
    bool IsWithinStore(string path);
}