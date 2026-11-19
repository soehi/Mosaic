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
}
