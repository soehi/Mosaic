using System.IO;
using System.Windows;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

// 拖出附件只做「复制导出」：不向 OLE 提供 Move 效果，任务内的附件记录与文件始终保留。
// 暂存副本只服务于拖放本身，DoDragDrop 返回后立即回收。
internal static class TaskAttachmentDrag
{
    // 应用内拖拽的标记格式：放置目标据此拒绝把自家附件再添加成重复附件。
    internal const string InternalFormatName = "Mosaic.TaskAttachmentTransfer";
    internal const DragDropEffects Effects = DragDropEffects.Copy;

    internal sealed record Staged(string Directory, string FilePath);

    internal static async Task<OperationResult<Staged>> StageAsync(
        Func<CancellationToken, Task<OperationResult<Stream>>> open,
        Guid attachmentId,
        string fileName,
        CancellationToken ct = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MosaicAttachments", attachmentId.ToString("D"));
        var filePath = Path.Combine(directory, fileName);
        try
        {
            var opened = await open(ct).ConfigureAwait(false);
            if (!opened.IsSuccess) return OperationResult<Staged>.Failure(opened.Error!);
            await using (opened.Value.ConfigureAwait(false))
            {
                Directory.CreateDirectory(directory);
                await using var target = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await opened.Value.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            return OperationResult<Staged>.Success(new(directory, filePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Release(directory);
            return OperationResult<Staged>.Failure(new(
                ErrorCodes.StorageUnavailable,
                exception.Message,
                MessageKey: "Tasks.Attachments.Error.WriteFailed"));
        }
        catch (OperationCanceledException)
        {
            Release(directory);
            throw;
        }
    }

    internal static DataObject CreateData(TaskAttachmentDto attachment, Staged staged)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { staged.FilePath });
        data.SetData(InternalFormatName, attachment.Id.ToString("D"));
        return data;
    }

    // 必须在 UI 线程同步调用：OLE 在 DoDragDrop 内部构造事件路由，换线程会抛跨线程异常。
    internal static DragDropEffects Run(DependencyObject source, DataObject data) =>
        DragDrop.DoDragDrop(source, data, Effects);

    internal static void Release(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 暂存目录清理失败不影响任务数据；系统临时目录会随后续清理回收。
        }
    }
}