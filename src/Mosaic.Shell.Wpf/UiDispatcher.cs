using System.Windows.Threading;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

public sealed class UiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();
    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;
}
