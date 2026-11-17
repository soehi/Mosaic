using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
namespace Mosaic.Tasks.Core.Internal;
internal sealed class TaskChangeSource(IDiagnosticSink diagnostics) : ITaskChangeSource
{
    private readonly object _sync=new();
    private readonly List<Action<TaskChange>> _observers=[];
    public IDisposable Subscribe(Action<TaskChange> observer) { lock(_sync) _observers.Add(observer); return new Registration(this,observer); }
    internal void Publish(TaskChange change)
    {
        Action<TaskChange>[] observers; lock(_sync) observers=_observers.ToArray();
        ThreadPool.QueueUserWorkItem(_=> { foreach(var observer in observers) { try { observer(change); } catch(Exception ex) {try{diagnostics.Report(Guid.NewGuid().ToString("N"),ex);}catch{}} } });
    }
    internal void Clear(){lock(_sync) _observers.Clear();}
    private sealed class Registration(TaskChangeSource owner,Action<TaskChange> observer):IDisposable
    { public void Dispose(){lock(owner._sync)owner._observers.Remove(observer);} }
}
