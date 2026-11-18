using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.Presentation.Contracts;
namespace Mosaic.Tasks.Wpf.Internal;
internal sealed class TaskDetachSource(IUiDispatcher dispatcher) : IFloatingDetachSource
{
    private readonly object _sync=new();
    private IFloatingWindowApi? _handler;
    public bool IsAvailable {get {lock(_sync)return _handler is not null;} }
    public event EventHandler? AvailabilityChanged;
    public IDisposable RegisterDetachHandler(IFloatingWindowApi handler)
    {
        lock(_sync) {if(_handler is not null) throw new InvalidOperationException("A detach handler is already registered.");_handler=handler;}
        Signal(); return new Registration(this,handler);
    }
    internal void Deactivate(){lock(_sync)_handler=null;Signal();}
    private void Signal()=>_ = dispatcher.InvokeAsync(()=>AvailabilityChanged?.Invoke(this,EventArgs.Empty));
    public Task<OperationResult<FloatingOpenResult>> RequestDetachAsync(FloatingContentReference reference,FloatingPlacementHint? hint=null,CancellationToken cancellationToken=default)
    {
        IFloatingWindowApi? handler;lock(_sync)handler=_handler;
        return handler is null?Task.FromResult(OperationResult<FloatingOpenResult>.Failure(new(ErrorCodes.CapabilityUnavailable,"小窗功能尚未启用或正在退出。"))):handler.OpenAsync(reference,hint,cancellationToken);
    }
    public void BeginPlacementPreview(FloatingContentReference reference,FloatingPlacementHint hint)
    {
        IFloatingWindowApi? handler;lock(_sync)handler=_handler;
        handler?.BeginPlacementPreview(reference,hint);
    }
    public void UpdatePlacementPreview(FloatingPlacementHint hint)
    {
        IFloatingWindowApi? handler;lock(_sync)handler=_handler;
        handler?.UpdatePlacementPreview(hint);
    }
    public void EndPlacementPreview()
    {
        IFloatingWindowApi? handler;lock(_sync)handler=_handler;
        handler?.EndPlacementPreview();
    }
    private sealed class Registration(TaskDetachSource source,IFloatingWindowApi handler):IDisposable
    {public void Dispose(){lock(source._sync){if(!ReferenceEquals(source._handler,handler))return;source._handler=null;}source.Signal();}}
}
