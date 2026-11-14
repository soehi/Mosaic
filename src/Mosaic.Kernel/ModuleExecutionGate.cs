using Mosaic.Contracts;

namespace Mosaic.Kernel;

public sealed class ModuleExecutionGate : IModuleExecutionGate
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _active;
    private bool _stopping;
    private int _leases;
    public void Activate()
    {
        lock (_sync)
        {
            if (_stopping || _active) throw new InvalidOperationException("Gate cannot be activated.");
            _active = true;
        }
    }
    public IDisposable? TryEnter()
    {
        lock (_sync)
        {
            if (!_active || _stopping) return null;
            _leases++;
            return new Lease(this);
        }
    }
    public Task StopAsync()
    {
        lock (_sync)
        {
            _stopping = true;
            _active = false;
            if (_leases == 0) _drained.TrySetResult();
            return _drained.Task;
        }
    }
    private void Release()
    {
        lock (_sync) { if (--_leases == 0 && _stopping) _drained.TrySetResult(); }
    }
    private sealed class Lease(ModuleExecutionGate owner) : IDisposable
    {
        private ModuleExecutionGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
