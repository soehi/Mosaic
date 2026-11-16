using Mosaic.Contracts;

namespace Mosaic.DesktopPresence.Contracts;

public enum DesktopCloseBehavior { Reside, Exit }
public enum EdgeActivationMode { Hover, SingleClick, DoubleClick }
public enum DesktopPresenceState { MainVisible, ParkPending, ResidentIdle, PopupLoading, PopupOpen, PopupError, Exiting }
public enum ExitRequestResult { Cancelled, Failed, Accepted }

public interface IDesktopWindowHost
{
    nint MainWindowHandle => 0;
    Task HideMainWindowAsync();
    Task RestoreMainWindowAsync();
    Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default);
}

public interface IDesktopPresenceApi
{
    DesktopPresenceState State { get; }
    DesktopCloseBehavior CloseBehavior { get; }
    Task<OperationResult> RequestParkAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RequestRestoreAsync(CancellationToken cancellationToken = default);
    Task<ExitRequestResult> RequestExitAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RequestPopupAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RequestDismissAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RequestRefreshAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface IDesktopPresenceSurface
{
    Task ShowEdgeAsync(CancellationToken cancellationToken);
    Task HideEdgeAsync();
    Task ShowPopupAsync(CancellationToken cancellationToken);
    Task ClosePopupAsync();
    Task StopAsync();
}
