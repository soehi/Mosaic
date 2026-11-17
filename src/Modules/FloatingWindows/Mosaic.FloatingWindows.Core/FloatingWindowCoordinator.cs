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
    private bool _stopping;
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
        var record = (previous ?? surface.CreateRecord(reference,hint)) with { IsOpen=true, Revision=(previous?.Revision ?? 0)+1 };
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
    public async Task<OperationResult> CloseAsync(FloatingContentReference reference,CancellationToken cancellationToken=default)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (_stopping) return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable,"正在退出，请稍候。"));
            if (!_open.TryGetValue(reference,out var record)) return OperationResult.Success();
            var saved=await SaveCoreAsync(surface.Snapshot(record) with { IsOpen=false },cancellationToken);
            if (!saved.IsSuccess) return saved;
            await surface.DestroyAsync(reference); _open.Remove(reference);
            return OperationResult.Success();
        }
        finally { _serial.Release(); }
    }
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
    public async Task<OperationResult> PrepareExitAsync(CancellationToken ct=default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            _stopping=true;
            foreach(var record in _open.Values.ToArray())
            {
                var saved=await SaveCoreAsync(surface.Snapshot(record),ct);
                if(!saved.IsSuccess) { _stopping=false; return saved; }
            }
            return OperationResult.Success();
        }
        finally {_serial.Release();}
    }
    public void Resume() => _stopping=false;
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
