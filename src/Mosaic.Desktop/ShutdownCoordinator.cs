using System.ComponentModel;
using System.Windows;
using Mosaic.Shell.Wpf;

namespace Mosaic.Desktop;

internal sealed class ShutdownCoordinator(ShellWindow shell, Bootstrapper bootstrapper, Func<Task> completed, Action<Exception> failure)
{
    private bool _closing;
    private bool _final;
    internal void Attach() => shell.Closing += Closing;
    private async void Closing(object? sender, CancelEventArgs e)
    {
        if (_final) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            await Task.Yield(); // Let WPF finish dispatching the canceled Closing event first.
            if (!await shell.CanCloseAsync()) return;
            shell.IsEnabled = false;
            var cleanup = CleanupAsync();
            while (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(10))) != cleanup)
            {
                if (MessageBox.Show(shell, "仍在等待已接受的操作完成。选择“是”继续等待；选择“否”强制退出（保存结果可能未知，重启后请确认）。", "正在关闭 Mosaic", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                    Environment.Exit(2);
            }
            await cleanup;
            _final = true; shell.Close(); await CompleteAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
            _final = true;
            try { await CleanupAsync(); } catch (Exception cleanup) { Report(cleanup); }
            try { shell.Close(); } catch (Exception closeError) { Report(closeError); }
            await CompleteAsync();
        }
        finally { _closing = false; }
    }
    private async Task CleanupAsync()
    {
        var errors = new List<Exception>();
        try { await shell.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        try { await bootstrapper.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private async Task CompleteAsync()
    {
        try { await completed(); }
        catch (Exception ex) { Report(ex); }
    }
    private void Report(Exception exception)
    {
        try { failure(exception); }
        catch { /* Keep the closing path alive so it can still release the window and process. */ }
    }
}
