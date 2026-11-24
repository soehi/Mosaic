using Mosaic.Contracts;

namespace Mosaic.Tasks.Contracts;

// Explicit cross-device transfer capability. Export reuses the same listing rules as the UI;
// ReplaceAllAsync overwrites the local task table in one transaction with the supplied rows,
// preserving identifiers, revisions, archive timestamps and creation receipts.
// The attachment members below exist only for the user-triggered backup upload/download path:
// the caller must own the returned stream and dispose it.
public interface ITaskTransferApi
{
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> ExportAllAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default);

    /// <summary>跨全部任务导出附件清单（不含内容），顺序为 added_at, id。</summary>
    Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ExportAttachmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>打开附件内容并同时给出 SHA-256（小写十六进制）；调用方负责释放 <see cref="TaskAttachmentExportDto.Content"/>。</summary>
    Task<OperationResult<TaskAttachmentExportDto>> OpenAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>把已下载到 <paramref name="sourcePath"/> 的内容写入受管存储并登记元数据；不改动任务 revision。</summary>
    Task<OperationResult> ImportAttachmentAsync(TaskAttachmentDto attachment, string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>删除本地附件行与文件；不存在视为成功（幂等）。</summary>
    Task<OperationResult> DeleteAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>删除不在 <paramref name="keepIds"/> 中的本地附件（行与文件），返回删除数量。用于按服务器清单镜像本地。</summary>
    Task<OperationResult<int>> RemoveAttachmentsNotInAsync(IReadOnlyCollection<Guid> keepIds, CancellationToken cancellationToken = default);

    /// <summary>扫描附件目录，删除"Guid 命名但不被任何附件行引用"的文件，返回删除数量。</summary>
    Task<OperationResult<int>> SweepOrphanAttachmentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>附件内容句柄：元数据、打开的只读流及其内容哈希。</summary>
public sealed record TaskAttachmentExportDto(TaskAttachmentDto Attachment, Stream Content, string Sha256);