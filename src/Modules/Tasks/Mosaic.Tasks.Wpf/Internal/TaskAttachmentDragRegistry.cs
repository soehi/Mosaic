using System.IO;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal interface ITaskAttachmentTransferRegistry
{
    void Put(TaskAttachmentDragTransfer transfer);
    bool TryGet(Guid attachmentId, out TaskAttachmentDragTransfer transfer);
    void CommitMove(Guid attachmentId);
    void CommitCopy(Guid attachmentId);
    void Discard(Guid attachmentId);
}

// 拖出暂存登记表。拖到应用内（任务附件区）时 WPF 不调用 OLE 目标，DoDragDrop 返回 None，
// 由放置目标自行完成移动/复制；拖到外部时 DoDragDrop 同步返回实际效果，按 Copy/Move 结算。
internal sealed class TaskAttachmentDragRegistry : ITaskAttachmentTransferRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TaskAttachmentDragTransfer> _pending = [];

    public void Put(TaskAttachmentDragTransfer transfer)
    {
        lock (_sync) _pending[transfer.AttachmentId] = transfer;
    }

    public bool TryGet(Guid attachmentId, out TaskAttachmentDragTransfer transfer)
    {
        lock (_sync) return _pending.TryGetValue(attachmentId, out transfer!);
    }

    public void CommitMove(Guid attachmentId) => Settle(attachmentId, deleteStaging: true);

    public void CommitCopy(Guid attachmentId) => Settle(attachmentId, deleteStaging: true);

    // 未发生放置（Esc、拖到空白处等）：暂存副本无引用，删除临时目录。
    public void Discard(Guid attachmentId) => Settle(attachmentId, deleteStaging: true);

    private void Settle(Guid attachmentId, bool deleteStaging)
    {
        TaskAttachmentDragTransfer? transfer;
        lock (_sync)
        {
            if (!_pending.Remove(attachmentId, out transfer)) return;
        }
        if (!deleteStaging || transfer.StagingDirectory.Length == 0) return;
        try { Directory.Delete(transfer.StagingDirectory, recursive: true); }
        catch { /* 临时目录由系统清理，删除失败不影响结果 */ }
    }
}
