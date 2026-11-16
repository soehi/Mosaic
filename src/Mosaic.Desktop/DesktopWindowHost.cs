using System.Windows;
using System.Windows.Interop;
using Mosaic.DesktopPresence.Contracts;

namespace Mosaic.Desktop;

internal sealed class DesktopWindowHost : IDesktopWindowHost
{
    internal Window? Window { get; set; }
    internal ShutdownCoordinator? Coordinator { get; set; }
    public nint MainWindowHandle => Window is { } window ? new WindowInteropHelper(window).Handle : 0;
    public Task HideMainWindowAsync()
    {
        (Window ?? throw new InvalidOperationException("Main window is not ready.")).Hide();
        return Task.CompletedTask;
    }
    public Task RestoreMainWindowAsync()
    {
        var window = Window ?? throw new InvalidOperationException("Main window is not ready.");
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show(); window.Activate();
        return Task.CompletedTask;
    }
    public Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default) =>
        Coordinator?.RequestExitAsync(cancellationToken) ?? Task.FromResult(ExitRequestResult.Failed);
}
