using Mosaic.Contracts;
using Mosaic.DesktopPresence.Contracts;

namespace Mosaic.DesktopPresence.Core;

public sealed class PresenceController(
    IDesktopWindowHost host, IDesktopPresenceSurface surface, IDiagnosticSink diagnostics) : IDesktopPresenceApi
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private bool _stopped;
    public DesktopPresenceState State { get; private set; } = DesktopPresenceState.MainVisible;
    public DesktopCloseBehavior CloseBehavior { get; private set; } = DesktopCloseBehavior.Reside;
    public void Configure(DesktopCloseBehavior behavior) => CloseBehavior = behavior;

    public Task<OperationResult> RequestParkAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        if (State != DesktopPresenceState.MainVisible) return;
        State = DesktopPresenceState.ParkPending;
        try
        {
            await surface.ShowEdgeAsync(cancellationToken);
            await host.HideMainWindowAsync();
            State = DesktopPresenceState.ResidentIdle;
        }
        catch
        {
            State = DesktopPresenceState.MainVisible;
            try { await host.RestoreMainWindowAsync(); }
            finally { await surface.HideEdgeAsync(); }
            throw;
        }
    }, cancellationToken);

    public Task<OperationResult> RequestRestoreAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        // A failed companion cleanup must not take away the user's route back to the main window.
        try { await surface.ClosePopupAsync(); } catch (Exception exception) { Report(exception); }
        await host.RestoreMainWindowAsync();
        State = DesktopPresenceState.MainVisible;
        await surface.HideEdgeAsync();
    }, cancellationToken);

    public Task<OperationResult> RequestPopupAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        if (State is DesktopPresenceState.MainVisible or DesktopPresenceState.ParkPending) return;
        if (State is DesktopPresenceState.PopupOpen or DesktopPresenceState.PopupError)
        {
            await surface.ClosePopupAsync();
            State = DesktopPresenceState.ResidentIdle;
            return;
        }
        State = DesktopPresenceState.PopupLoading;
        try
        {
            await surface.ShowPopupAsync(cancellationToken);
            State = DesktopPresenceState.PopupOpen;
        }
        catch { State = DesktopPresenceState.PopupError; throw; }
    }, cancellationToken);

    public Task<OperationResult> RequestDismissAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await surface.ClosePopupAsync();
        if (State != DesktopPresenceState.MainVisible) State = DesktopPresenceState.ResidentIdle;
    }, cancellationToken);

    public Task<OperationResult> RequestRefreshAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        if (State is not (DesktopPresenceState.PopupOpen or DesktopPresenceState.PopupError)) return;
        await surface.ClosePopupAsync();
        State = DesktopPresenceState.PopupLoading;
        try { await surface.ShowPopupAsync(cancellationToken); State = DesktopPresenceState.PopupOpen; }
        catch { State = DesktopPresenceState.PopupError; throw; }
    }, cancellationToken);

    public async Task<ExitRequestResult> RequestExitAsync(CancellationToken cancellationToken = default)
    {
        await _requests.WaitAsync(cancellationToken);
        try
        {
            if (_stopped || State == DesktopPresenceState.Exiting) return ExitRequestResult.Accepted;
            var result = await host.RequestApplicationExitAsync(cancellationToken);
            if (result == ExitRequestResult.Accepted) State = DesktopPresenceState.Exiting;
            return result;
        }
        catch (Exception exception) { Report(exception); return ExitRequestResult.Failed; }
        finally { _requests.Release(); }
    }

    public async Task<OperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _requests.WaitAsync(cancellationToken);
        try
        {
            if (_stopped) return OperationResult.Success();
            _stopped = true;
            State = DesktopPresenceState.Exiting;
            await surface.StopAsync();
            return OperationResult.Success();
        }
        catch (Exception exception) { Report(exception); return Failure(); }
        finally { _requests.Release(); }
    }

    private async Task<OperationResult> RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken);
        try
        {
            if (_stopped || State == DesktopPresenceState.Exiting)
                return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "Desktop presence is stopping."));
            await action();
            return OperationResult.Success();
        }
        catch (Exception exception) { Report(exception); return Failure(); }
        finally { _requests.Release(); }
    }

    private void Report(Exception exception)
    {
        try { diagnostics.Report(Guid.NewGuid().ToString("N"), exception); } catch { }
    }
    private static OperationResult Failure() => OperationResult.Failure(new(ErrorCodes.InternalError, "无法完成驻留操作，请恢复主窗口后重试。"));
}

public static class PresenceActivationRules
{
    public static bool Matches(EdgeActivationMode mode, int clickCount, bool hoverElapsed = false) => mode switch
    {
        EdgeActivationMode.Hover => hoverElapsed,
        EdgeActivationMode.SingleClick => clickCount == 1,
        EdgeActivationMode.DoubleClick => clickCount == 2,
        _ => false
    };
}
