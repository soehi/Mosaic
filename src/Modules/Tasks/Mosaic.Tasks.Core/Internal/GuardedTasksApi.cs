using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class GuardedTasksApi(TaskService service, IModuleExecutionGate gate, IDiagnosticSink diagnostics) : ITasksApi
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
    public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) =>
        Invoke(request is { Title: not null, Notes: not null }, () => service.CreateAsync(request, cancellationToken), cancellationToken);
    public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => Invoke(id != Guid.Empty, () => service.GetAsync(id, cancellationToken), cancellationToken);
    public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
        Invoke(query is not null && Enum.IsDefined(query.Filter), () => service.ListAsync(query!, cancellationToken), cancellationToken);
    public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) =>
        Invoke(request is { Title: not null, Notes: not null } && Valid(request.Id, request.ExpectedRevision), () => service.UpdateAsync(request!, cancellationToken), cancellationToken);
    public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) =>
        Invoke(request is not null && Valid(request.Id, request.ExpectedRevision), () => service.SetCompletedAsync(request!, cancellationToken), cancellationToken);
    public async Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default)
    {
        var result = await Invoke(request is not null && Valid(request.Id, request.ExpectedRevision), async () =>
        {
            var deleted = await service.DeleteAsync(request!, cancellationToken).ConfigureAwait(false);
            return deleted.IsSuccess ? OperationResult<bool>.Success(true) : OperationResult<bool>.Failure(deleted.Error!);
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }
}
