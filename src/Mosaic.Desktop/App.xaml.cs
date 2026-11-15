using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Mosaic.Presentation.Wpf;
using Mosaic.Shell.Wpf;

namespace Mosaic.Desktop;

public partial class App : Application
{
    private readonly Bootstrapper _bootstrapper = new();
    private SingleInstance? _instance;
    private AppPaths? _paths;
    private ShellWindow? _shell;
    private bool _fatal;
    private int _shutdownRequested;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MosaicThemeManager.Attach(Resources);
        DispatcherUnhandledException += OnDispatcherException;
        var diagnostic = new Window
        {
            Title = "Mosaic · 启动",
            Width = 580,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "正在准备本机工作空间…", Margin = new Thickness(32), FontSize = 18, TextWrapping = TextWrapping.Wrap }
        };
        MainWindow = diagnostic;
        diagnostic.Show();
        diagnostic.Closing += (_, args) => { if (!_fatal) args.Cancel = true; };
        ShellWindow? shell = null;
        try
        {
            _paths = AppPaths.FromArguments(e.Args);
            _instance = new(_paths.Root, ReportFailure, ReadSingleInstanceRetry(_paths));
            if (!_instance.IsPrimary) { await _instance.NotifyAsync(); await FinishAsync(); return; }
            _instance.Listen(() => Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is { } window) { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Show(); window.Activate(); }
            }));
            shell = await _bootstrapper.StartAsync(_paths, new UiDispatcher(Dispatcher), new UserDialogService());
            _shell = shell;
            await shell.InitializeAsync();
            new ShutdownCoordinator(shell, _bootstrapper, FinishAsync, ReportFailure).Attach();
            MainWindow = shell;
            shell.Show(); _fatal = true; diagnostic.Close(); _fatal = false;
        }
        catch (Exception ex)
        {
            if (shell is not null)
            {
                try { await shell.DisposeAsync(); } catch (Exception cleanup) { ReportFailure(cleanup); }
                try { shell.Close(); } catch (Exception cleanup) { ReportFailure(cleanup); }
                if (ReferenceEquals(MainWindow, shell)) MainWindow = diagnostic;
                _shell = null;
            }
            try { await _bootstrapper.DisposeAsync(); } catch (Exception cleanup) { ReportFailure(cleanup); }
            ReportFailure(ex); _fatal = true;
            diagnostic.Title = "Mosaic · 无法启动";
            System.Windows.Automation.AutomationProperties.SetName(diagnostic, diagnostic.Title);
            diagnostic.Content = new TextBlock
            {
                Margin = new Thickness(32),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Text = ex is ConfigurationException ? ex.Message + "\n\n修正配置后重新启动。" : "本机工作空间启动失败。请检查数据目录权限或查看 Logs 中的诊断记录后重试。"
            };
            diagnostic.Hide(); diagnostic.Show();
            diagnostic.Closed += async (_, _) => await FinishAsync();
        }
    }
    private void ReportFailure(Exception exception)
    {
        if (_paths is null) return;
        try
        {
            using var provider = new FileLoggerProvider(_paths.Logs);
            provider.CreateLogger("Desktop").LogError(exception, "Desktop lifecycle failure");
        }
        catch { /* Diagnostics must not break the failure path it is reporting. */ }
    }
    private static int ReadSingleInstanceRetry(AppPaths paths)
    {
        try
        {
            var configuration = AppConfiguration.Load(paths);
            return configuration.Settings!.TryGetValue("diagnostics.singleInstanceRetryMilliseconds", out var element) &&
                element.TryGetInt32(out var value) && value > 0
                ? value
                : 250;
        }
        catch
        {
            return 250;
        }
    }
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _fatal = true; e.Handled = true; ReportFailure(e.Exception);
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        try
        {
            if (MainWindow is { } window) window.IsEnabled = false;
            MessageBox.Show("Mosaic 遇到未预期的错误，将受控关闭。请重新启动并确认最近的保存结果。", "Mosaic");
        }
        catch (Exception ex) { ReportFailure(ex); }
        _ = StopAfterFailureAsync();
    }
    private async Task StopAfterFailureAsync()
    {
        if (_shell is not null)
        {
            try { await _shell.DisposeAsync(); } catch (Exception ex) { ReportFailure(ex); }
        }
        try { await _bootstrapper.DisposeAsync(); } catch (Exception ex) { ReportFailure(ex); }
        await FinishAsync();
    }
    private async Task FinishAsync()
    {
        _fatal = true;
        Interlocked.Exchange(ref _shutdownRequested, 1);
        if (_instance is not null)
        {
            try { await _instance.DisposeAsync(); }
            catch (Exception ex) { ReportFailure(ex); }
            finally { _instance = null; }
        }
        try { Shutdown(); }
        catch (Exception ex) { ReportFailure(ex); }
    }
}
