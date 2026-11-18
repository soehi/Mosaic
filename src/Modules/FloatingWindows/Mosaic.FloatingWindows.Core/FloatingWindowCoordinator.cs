using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.FloatingWindows.Persistence.Contracts;
namespace Mosaic.FloatingWindows.Core;

// Serializes Opening/Open/Closing and persistence, including requests accepted before exit.
public sealed class FloatingWindowCoordinator(IFloatingWindowRepository repository, IFloatingWindowSurface surface,
    Func<int> maximum, IDiagnosticSink diagnostics) : IFloatingWindowApi
{
    private readonly SemaphoreSlim _serial = new(1);
    private readonly Dictionary<FloatingContentReference,FloatingWindowRecord> _open = [];
    private readonly Dictionary<FloatingContentReference, Task<OperationResult>> _closing = [];
    private readonly SemaphoreSlim _exit = new(1);
    private volatile bool _stopping;
    private bool _exitPrepared;
    public async Task<OperationResult<FloatingOpenResult>> OpenAsync(FloatingContentReference reference, FloatingPlacementHint? hint = null, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken);
        try { return await OpenCoreAsync(reference,hint,null,cancellationToken); }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){return OperationResult<FloatingOpenResult>.Failure(new(ErrorCodes.Canceled,"开窗已取消。"));}
        catch(Exception ex){Report(ex.Message);return Reject("无法打开小窗："+ex.Message);}
        finally { _serial.Release(); }
    }
    private async Task<OperationResult<FloatingOpenResult>> OpenCoreAsync(FloatingContentReference reference, FloatingPlacementHint? hint, FloatingWindowRecord? restore, CancellationToken ct)
    {
        if (_stopping) return Reject("窗口模块正在退出。");
        if (string.IsNullOrWhiteSpace(reference.ProviderId) || string.IsNullOrWhiteSpace(reference.ItemKey) || reference.ProviderId.Length > 100 || reference.ItemKey.Length > 200) return Reject("内容引用无效。");
        if (_open.ContainsKey(reference)) { surface.Activate(reference); return OperationResult<FloatingOpenResult>.Success(new(FloatingOpenDisposition.ActivatedExisting)); }
        if (_open.Count >= maximum()) return Reject("已达到悬浮窗口数量上限，请先关闭部分小窗。");
        var loaded = await repository.LoadAsync(ct);
        if (!loaded.IsSuccess) return OperationResult<FloatingOpenResult>.Failure(loaded.Error!);
        var previous = restore ?? loaded.Value.FirstOrDefault(item => item.Reference == reference);
        // A detach hint always wins over the saved position; sizes, mode and topmost still come from the old record.
        var placement = hint is null ? null : surface.CreateRecord(reference, hint);
        var placed = previous is null ? placement ?? surface.CreateRecord(reference, null)
            : placement is null ? previous
            : previous with { MonitorId = placement.MonitorId, X = placement.X, Y = placement.Y };
        var record = placed with { IsOpen=true, Revision=(previous?.Revision ?? 0)+1 };
        var saved = await repository.SaveAsync(record, record.Revision-1,ct);
        if (!saved.IsSuccess) return OperationResult<FloatingOpenResult>.Failure(saved.Error!);
        try
        {
            var shown = await surface.ShowAsync(record,ct);
            if (!shown.IsSuccess)
            {
                await surface.DestroyAsync(reference);
                // Unavailable providers/read errors keep restoration eligibility; confirmed missing content does not.
                if (restore is null || shown.Error!.Code == ErrorCodes.NotFound)
                {
                    var rollback = await repository.SaveAsync(record with { IsOpen=false, Revision=record.Revision+1 },record.Revision,CancellationToken.None);
                    if (!rollback.IsSuccess) Report(rollback.Error!.Message);
                }
                return OperationResult<FloatingOpenResult>.Failure(shown.Error!);
            }
            _open.Add(reference,record);
            return OperationResult<FloatingOpenResult>.Success(new(FloatingOpenDisposition.Created));
        }
        catch (Exception ex)
        {
            await surface.DestroyAsync(reference);
            Report(ex.Message);
            if(restore is null)
            {
                var rolledBack=await repository.SaveAsync(record with{IsOpen=false,Revision=record.Revision+1},record.Revision,CancellationToken.None);
                if(!rolledBack.IsSuccess)Report(rolledBack.Error!.Message);
            }
            return ex is OperationCanceledException && ct.IsCancellationRequested
                ? OperationResult<FloatingOpenResult>.Failure(new(ErrorCodes.Canceled,"开窗已取消。"))
                : Reject("无法创建小窗，可重试。");
        }
    }
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            var loaded = await repository.LoadAsync(ct);
            if (!loaded.IsSuccess) { Report(loaded.Error!.Message); return; }
            foreach (var record in loaded.Value.Where(item=>item.IsOpen))
            {
                var result=await OpenCoreAsync(record.Reference,null,record,ct);
                if (!result.IsSuccess) Report(result.Error!.Message);
            }
        }
        finally { _serial.Release(); }
    }
    public void BeginPlacementPreview(FloatingContentReference reference,FloatingPlacementHint hint)
    {
        if(!_stopping)surface.BeginPreview(reference,hint);
    }
    public void UpdatePlacementPreview(FloatingPlacementHint hint)
    {
        if(!_stopping)surface.UpdatePreview(hint);
    }
    public void EndPlacementPreview()=>surface.EndPreview();
    public async Task<OperationResult> CloseAsync(FloatingContentReference reference,CancellationToken cancellationToken=default)
    {
        TaskCompletionSource<OperationResult>? request = null;
        Task<OperationResult> pending;
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (!_closing.TryGetValue(reference, out pending!))
            {
                if (_stopping) return Canceled();
                if (!_open.ContainsKey(reference)) return OperationResult.Success();
                request = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _closing.Add(reference, pending = request.Task);
            }
        }
        finally { _serial.Release(); }
        if (request is not null) await CompleteCloseAsync(reference, request, cancellationToken);
        return await pending.WaitAsync(cancellationToken);
    }

    private async Task CompleteCloseAsync(FloatingContentReference reference, TaskCompletionSource<OperationResult> request, CancellationToken ct)
    {
        var destroyed = false;
        OperationResult result;
        try
        {
            result = await surface.PrepareCloseAsync(reference, ct);
            if (result.IsSuccess)
            {
                await _serial.WaitAsync(ct);
                try
                {
                    if (_open.TryGetValue(reference, out var record))
                    {
                        result = await SaveCoreAsync(surface.Snapshot(record) with { IsOpen = false }, ct);
                        if (result.IsSuccess)
                        {
                            _open.Remove(reference);
                            destroyed = true;
                            await surface.DestroyAsync(reference);
                        }
                    }
                }
                finally { _serial.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = Canceled(); }
        catch (Exception ex) { Report(ex.Message); result = OperationResult.Failure(new(ErrorCodes.InternalError, "无法关闭小窗，请重试。")); }
        try { if (!destroyed) await surface.CancelClosePreparationAsync(reference); }
        catch (Exception ex) { Report(ex.Message); }
        await _serial.WaitAsync();
        try { _closing.Remove(reference); request.SetResult(result); }
        finally { _serial.Release(); }
    }

    private static OperationResult Canceled() => OperationResult.Failure(new(ErrorCodes.Canceled, "已取消关闭小窗。"));
    public async Task<OperationResult> ChangeAsync(FloatingContentReference reference,Func<FloatingWindowRecord,FloatingWindowRecord> change,CancellationToken ct=default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            if (_stopping || !_open.TryGetValue(reference,out var record)) return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable,"窗口不可用。"));
            var saved=await SaveCoreAsync(change(record),ct);
            if(saved.IsSuccess) surface.Apply(_open[reference]);
            return saved;
        }
        finally { _serial.Release(); }
    }
    private async Task<OperationResult> SaveCoreAsync(FloatingWindowRecord record,CancellationToken ct)
    {
        var next=record with { Revision=checked(record.Revision+1) };
        var saved=await repository.SaveAsync(next,record.Revision,ct);
        if(saved.IsSuccess) _open[record.Reference]=next;
        return saved;
    }
    public Task<OperationResult> PrepareExitAsync(CancellationToken ct=default) => PrepareExitAsync(null, ct);

    public async Task<OperationResult> PrepareExitAsync(Func<Task>? flush, CancellationToken ct=default)
    {
        await _exit.WaitAsync(ct);
        var reserved = new List<FloatingContentReference>();
        try
        {
            if (_exitPrepared) return OperationResult.Success();
            Task<OperationResult>[] closing;
            await _serial.WaitAsync(ct);
            try { _stopping = true; closing = _closing.Values.ToArray(); }
            finally { _serial.Release(); }
            var completed = await Task.WhenAll(closing);
            ct.ThrowIfCancellationRequested();
            var failed = completed.FirstOrDefault(result => !result.IsSuccess);
            if (failed is not null) return failed;

            foreach (var reference in _open.Keys.ToArray())
            {
                reserved.Add(reference);
                var prepared = await surface.PrepareCloseAsync(reference, ct);
                if (!prepared.IsSuccess) return prepared;
            }
            if (flush is not null) await flush();
            await _serial.WaitAsync(ct);
            try
            {
                foreach (var record in _open.Values.ToArray())
                {
                    var saved = await SaveCoreAsync(surface.Snapshot(record), ct);
                    if (!saved.IsSuccess) return saved;
                }
                _exitPrepared = true;
                return OperationResult.Success();
            }
            finally { _serial.Release(); }
        }
        finally
        {
            try
            {
                if (!_exitPrepared)
                {
                    foreach (var reference in reserved) await surface.CancelClosePreparationAsync(reference);
                }
            }
            finally
            {
                if (!_exitPrepared) _stopping = false;
                _exit.Release();
            }
        }
    }
    public async Task ResumeAsync()
    {
        await _exit.WaitAsync();
        try
        {
            if (_exitPrepared)
                foreach (var reference in _open.Keys.ToArray()) await surface.CancelClosePreparationAsync(reference);
            _exitPrepared = false;
            _stopping = false;
        }
        finally { _exit.Release(); }
    }

    public async Task StopAsync()
    {
        var saved=await PrepareExitAsync();
        if(!saved.IsSuccess) throw new InvalidOperationException(saved.Error!.Message);
        await _serial.WaitAsync();
        try { foreach(var reference in _open.Keys.ToArray()) await surface.DestroyAsync(reference); _open.Clear(); }
        finally {_serial.Release();}
    }
    public async Task StopWithoutSavingAsync()
    {
        await _serial.WaitAsync();
        try{_stopping=true;foreach(var reference in _open.Keys.ToArray())await surface.DestroyAsync(reference);_open.Clear();}
        finally{_serial.Release();}
    }
    private void Report(string message) { try {diagnostics.Report(Guid.NewGuid().ToString("N"),new InvalidOperationException(message));}catch {} }
    private static OperationResult<FloatingOpenResult> Reject(string message)=>OperationResult<FloatingOpenResult>.Failure(new(ErrorCodes.CapabilityUnavailable,message));
}
