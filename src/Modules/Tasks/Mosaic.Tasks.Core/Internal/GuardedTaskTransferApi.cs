using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class GuardedTaskTransferApi(TaskTransferService service, IModuleExecutionGate gate, IDiagnosticSink diagnostics) : ITaskTransferApi
{
    private async Task<OperationResult<T>> Invoke<T>(Func<Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
    {
        using var lease = gate.TryEnter();
        if (lease is null) return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "任务功能尚未启用或正在关闭。"));
        if (cancellationToken.IsCancellationRequested) return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。"));
        try { return await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。")); }
        catch (Exception ex)
        {
            var id = Guid.NewGuid().ToString("N");
            diagnostics.Report(id, ex);
            return OperationResult<T>.Failure(new(ErrorCodes.InternalError, "操作未完成，请重试。", CorrelationId: id));
        }
    }

    public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ExportAllAsync(CancellationToken cancellationToken = default) =>
        Invoke(() => service.ExportAllAsync(cancellationToken), cancellationToken);

    public async Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default)
    {
        var result = await Invoke<object?>(async () =>
        {
            var replaced = await service.ReplaceAllAsync(tasks, cancellationToken).ConfigureAwait(false);
            return replaced.IsSuccess ? OperationResult<object?>.Success(null) : OperationResult<object?>.Failure(replaced.Error!);
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ExportAttachmentsAsync(CancellationToken cancellationToken = default) =>
        Invoke(() => service.ExportAttachmentsAsync(cancellationToken), cancellationToken);

    public Task<OperationResult<TaskAttachmentExportDto>> OpenAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
        Invoke(() => service.OpenAttachmentAsync(attachmentId, cancellationToken), cancellationToken);

    public Task<OperationResult> ImportAttachmentAsync(TaskAttachmentDto attachment, string sourcePath, CancellationToken cancellationToken = default) =>
        ToResult(() => Invoke<object?>(async () =>
        {
            var imported = await service.ImportAttachmentAsync(attachment, sourcePath, cancellationToken).ConfigureAwait(false);
            return imported.IsSuccess ? OperationResult<object?>.Success(null) : OperationResult<object?>.Failure(imported.Error!);
        }, cancellationToken));

    public Task<OperationResult> DeleteAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
        ToResult(() => Invoke<object?>(async () =>
        {
            var deleted = await service.DeleteAttachmentAsync(attachmentId, cancellationToken).ConfigureAwait(false);
            return deleted.IsSuccess ? OperationResult<object?>.Success(null) : OperationResult<object?>.Failure(deleted.Error!);
        }, cancellationToken));

    public Task<OperationResult<int>> RemoveAttachmentsNotInAsync(IReadOnlyCollection<Guid> keepIds, CancellationToken cancellationToken = default) =>
        Invoke(() => service.RemoveAttachmentsNotInAsync(keepIds, cancellationToken), cancellationToken);

    public Task<OperationResult<int>> SweepOrphanAttachmentsAsync(CancellationToken cancellationToken = default) =>
        Invoke(() => service.SweepOrphanAttachmentsAsync(cancellationToken), cancellationToken);

    private static async Task<OperationResult> ToResult(Func<Task<OperationResult<object?>>> pending)
    {
        var result = await pending().ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }
}