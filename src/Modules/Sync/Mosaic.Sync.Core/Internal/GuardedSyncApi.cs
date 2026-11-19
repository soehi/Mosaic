using Mosaic.Contracts;
using Mosaic.Sync.Contracts;

namespace Mosaic.Sync.Core.Internal;

internal sealed class GuardedSyncApi(SyncService service, IModuleExecutionGate gate, IDiagnosticSink diagnostics) : ISyncApi
{
    public SyncState State => service.State;

    public event EventHandler? StateChanged
    {
        add => service.StateChanged += value;
        remove => service.StateChanged -= value;
    }

    private async Task<OperationResult<T>> Invoke<T>(Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
    {
        using var lease = gate.TryEnter();
        if (lease is null) return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "备份功能尚未启用或正在关闭。"));
        if (cancellationToken.IsCancellationRequested) return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。"));
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return OperationResult<T>.Failure(new(ErrorCodes.Canceled, "操作已取消。")); }
        catch (Exception exception)
        {
            var id = Guid.NewGuid().ToString("N");
            diagnostics.Report(id, exception);
            return OperationResult<T>.Failure(new(ErrorCodes.InternalError, "备份操作未完成，请重试。", CorrelationId: id));
        }
    }

    public Task<OperationResult<BackupSnapshotDto?>> RefreshLatestAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.RefreshLatestAsync, cancellationToken);

    public Task<OperationResult<BackupUploadResult>> UploadAsync(bool includeSettings, CancellationToken cancellationToken = default) =>
        Invoke(token => service.UploadAsync(includeSettings, token), cancellationToken);

    public Task<OperationResult<RestoreSummary>> DownloadAndReplaceAsync(CancellationToken cancellationToken = default) =>
        Invoke(service.DownloadAndReplaceAsync, cancellationToken);
}
