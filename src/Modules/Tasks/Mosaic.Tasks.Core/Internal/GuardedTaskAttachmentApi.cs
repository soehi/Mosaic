using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class GuardedTaskAttachmentApi(TaskAttachmentService service, IModuleExecutionGate gate, IDiagnosticSink diagnostics) : ITaskAttachmentApi
{
    private static OperationError Invalid() => new(ErrorCodes.InvalidRequest, "请求参数无效。");
    private static bool Valid(Guid id, long revision) => id != Guid.Empty && revision > 0 && revision < long.MaxValue;
    private async Task<OperationResult<T>> Invoke<T>(bool valid, Func<Task<OperationResult<T>>> operation, CancellationToken ct)
    {
        using var lease = gate.TryEnter();
        if (lease is null) return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "任务功能尚未启用或正在关闭。"));
        if (!valid) return OperationResult<T>.Failure(Invalid());
        if (ct.IsCancellationRequested) return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。"));
        try { return await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。")); }
        catch (Exception ex)
        {
            var id = Guid.NewGuid().ToString("N");
            diagnostics.Report(id, ex);
            return OperationResult<T>.Failure(new(ErrorCodes.InternalError, "操作未完成，请重试。", CorrelationId: id));
        }
    }

    public Task<OperationResult<IReadOnlyList<TaskAttachmentDto>>> ListAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        Invoke(taskId != Guid.Empty, () => service.ListAsync(taskId, cancellationToken), cancellationToken);

    public Task<OperationResult<TaskItemDto>> AddAsync(AddTaskAttachmentRequest request, CancellationToken cancellationToken = default) =>
        Invoke(request is { SourcePath: not null } && Valid(request.TaskId, request.ExpectedRevision) && request.SourcePath.Trim().Length > 0,
            () => service.AddAsync(request!, cancellationToken), cancellationToken);

    public Task<OperationResult<TaskItemDto>> ExportAsync(ExportTaskAttachmentRequest request, CancellationToken cancellationToken = default) =>
        Invoke(request is { TargetDirectory: not null } && Valid(request.TaskId, request.ExpectedRevision) && request.AttachmentId != Guid.Empty &&
            request.TargetDirectory.Trim().Length > 0 && Enum.IsDefined(request.Mode),
            () => service.ExportAsync(request!, cancellationToken), cancellationToken);

    public Task<OperationResult<TaskItemDto>> RemoveAsync(RemoveTaskAttachmentRequest request, CancellationToken cancellationToken = default) =>
        Invoke(request is not null && Valid(request.TaskId, request.ExpectedRevision) && request.AttachmentId != Guid.Empty,
            () => service.RemoveAsync(request!, cancellationToken), cancellationToken);
}
