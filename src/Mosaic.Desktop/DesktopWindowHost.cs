using System.Windows;
using System.Windows.Interop;
using Mosaic.DesktopPresence.Contracts;

namespace Mosaic.Desktop;

internal sealed class DesktopWindowHost : IDesktopWindowHost
{
    private Window? _window;
    internal Window? Window
    {
        get => _window;
        set
        {
            if (_window is not null) Detach(_window);
            _window = value;
            if (_window is not null) Attach(_window);
        }
    }
    internal ShutdownCoordinator? Coordinator { get; set; }
    public nint MainWindowHandle => _window is { } window ? new WindowInteropHelper(window).Handle : 0;
    public MainWindowPresentation CurrentPresentation => _window is not { } window ? MainWindowPresentation.Visible
        : !window.IsVisible ? MainWindowPresentation.Hidden
        : window.WindowState == WindowState.Minimized ? MainWindowPresentation.Minimized
        : MainWindowPresentation.Visible;
    public event EventHandler? PresentationChanged;

    private void Attach(Window window)
    {
        window.StateChanged += OnStateChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
    }
    private void Detach(Window window)
    {
        window.StateChanged -= OnStateChanged;
        window.IsVisibleChanged -= OnVisibilityChanged;
    }
    private void OnStateChanged(object? sender, EventArgs e) => PresentationChanged?.Invoke(this, EventArgs.Empty);
    private void OnVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) =>
        PresentationChanged?.Invoke(this, EventArgs.Empty);

    public Task HideMainWindowAsync()
    {
        (_window ?? throw new InvalidOperationException("Main window is not ready.")).Hide();
        return Task.CompletedTask;
    }
    public Task RestoreMainWindowAsync()
    {
        var window = _window ?? throw new InvalidOperationException("Main window is not ready.");
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show(); window.Activate();
        return Task.CompletedTask;
    }
    public Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default) =>
        Coordinator?.RequestExitAsync(cancellationToken) ?? Task.FromResult(ExitRequestResult.Failed);
}
