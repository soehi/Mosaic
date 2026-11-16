using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.Shell.Wpf;

namespace Mosaic.Desktop;

internal sealed class ShutdownCoordinator(ShellWindow shell, Bootstrapper bootstrapper, Func<Task> completed, Action<Exception> failure)
{
    private bool _requesting;
    private bool _accepted;
    private bool _final;
    private Task? _cleanup;
    internal void Attach() => shell.Closing += Closing;

    private async void Closing(object? sender, CancelEventArgs e)
    {
        if (_final) return;
        e.Cancel = true;
        if (_accepted || _requesting) return;
        try
        {
            await Task.Yield();
            if (bootstrapper.Presence is { CloseBehavior: DesktopCloseBehavior.Reside } presence)
            {
                var parked = await presence.RequestParkAsync();
                if (!parked.IsSuccess)
                    MessageBox.Show(shell, parked.Error!.Message, "Mosaic", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else await RequestExitAsync();
        }
        catch (Exception ex) { Report(ex); }
    }

    // Acceptance and cleanup are separate so a module never waits on its own Exit request.
    internal async Task<ExitRequestResult> RequestExitAsync(CancellationToken cancellationToken = default)
    {
        if (_accepted) return ExitRequestResult.Accepted;
        if (_requesting) return ExitRequestResult.Cancelled;
        _requesting = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await shell.CanCloseAsync()) return ExitRequestResult.Cancelled;
            cancellationToken.ThrowIfCancellationRequested();
            _accepted = true;
            shell.IsEnabled = false;
            _ = shell.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { _cleanup ??= FinishExitAsync(); }));
            return ExitRequestResult.Accepted;
        }
        catch (Exception ex) { Report(ex); return ExitRequestResult.Failed; }
        finally { _requesting = false; }
    }

    internal Task ForceExitAsync()
    {
        _accepted = true;
        return _cleanup ??= FinishExitAsync();
    }

    private async Task FinishExitAsync()
    {
        try
        {
            var cleanup = CleanupAsync();
            while (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(10))) != cleanup)
            {
                shell.Show();
                if (MessageBox.Show(shell,
                    DesktopLocalization.Localize(
                        "Desktop.Shutdown.Pending",
                        "仍在等待已接受的操作完成。选择“是”继续等待；选择“否”强制退出（保存结果可能未知，重启后请确认）。"),
                    DesktopLocalization.Localize("Desktop.Shutdown.Title", "正在关闭 Mosaic"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.No)
                    Environment.Exit(2);
            }
            await cleanup;
        }
        catch (Exception ex) { Report(ex); }
        finally
        {
            _final = true;
            try { shell.Close(); } catch (Exception ex) { Report(ex); }
            try { await completed(); } catch (Exception ex) { Report(ex); }
        }
    }

    private async Task CleanupAsync()
    {
        var errors = new List<Exception>();
        if (bootstrapper.Presence is { } presence)
        {
            try { await presence.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        try { await shell.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        try { await bootstrapper.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private void Report(Exception exception)
    {
        try { failure(exception); } catch { }
    }
}
