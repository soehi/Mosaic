using Mosaic.Contracts;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.DesktopPresence.Core;

namespace Mosaic.DesktopPresence.Core.Tests;

public sealed class PresenceTests
{
    private sealed class Host(List<string> calls) : IDesktopWindowHost
    {
        internal ExitRequestResult ExitResult = ExitRequestResult.Cancelled;
        internal int Exits;
        internal MainWindowPresentation Presentation = MainWindowPresentation.Visible;
        public MainWindowPresentation CurrentPresentation => Presentation;
        public event EventHandler? PresentationChanged;
        internal void Appear(MainWindowPresentation presentation)
        { Presentation = presentation; PresentationChanged?.Invoke(this, EventArgs.Empty); }
        public Task HideMainWindowAsync() { calls.Add("hide-main"); return Task.CompletedTask; }
        public Task RestoreMainWindowAsync() { calls.Add("restore-main"); return Task.CompletedTask; }
        public Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default)
        { Exits++; return Task.FromResult(ExitResult); }
    }
    private sealed class Surface(List<string> calls) : IDesktopPresenceSurface
    {
        internal bool FailEdge;
        internal bool FailPopup;
        internal bool FailClose;
        internal int Stops;
        internal TaskCompletionSource? EdgeGate;
        public async Task ShowEdgeAsync(CancellationToken cancellationToken)
        {
            calls.Add("edge");
            if (FailEdge) throw new IOException("edge unavailable");
            if (EdgeGate is not null) await EdgeGate.Task.WaitAsync(cancellationToken);
        }
        public Task HideEdgeAsync() { calls.Add("hide-edge"); return Task.CompletedTask; }
        public Task SetEdgeVisibleAsync(bool visible)
        { calls.Add(visible ? "show-edge-if-created" : "hide-edge-if-shown"); return Task.CompletedTask; }
        public Task ShowPopupAsync(CancellationToken cancellationToken)
        { calls.Add("popup"); if (FailPopup) throw new IOException("query failure"); return Task.CompletedTask; }
        public Task ClosePopupAsync() { calls.Add("close-popup"); if (FailClose) throw new IOException("cleanup failed"); return Task.CompletedTask; }
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
    }
    private sealed class Diagnostics : IDiagnosticSink
    {
        internal int Count;
        public void Report(string correlationId, Exception exception) => Count++;
    }

    [Fact]
    public async Task Park_restore_keep_host_and_order_entry_before_hiding_main()
    {
        var calls = new List<string>();
        var controller = new PresenceController(new Host(calls), new Surface(calls), new Diagnostics());
        Assert.True((await controller.RequestParkAsync()).IsSuccess);
        Assert.Equal(DesktopPresenceState.ResidentIdle, controller.State);
        Assert.Equal(new[] { "edge", "hide-main" }, calls);
        await controller.RequestPopupAsync();
        await controller.RequestRestoreAsync();
        Assert.Equal(DesktopPresenceState.MainVisible, controller.State);
        Assert.Equal(new[] { "close-popup", "restore-main", "hide-edge-if-shown" }, calls.TakeLast(3));
    }

    [Fact]
    public async Task Companion_cleanup_failure_does_not_block_restoring_main()
    {
        var calls = new List<string>(); var diagnostics = new Diagnostics();
        var controller = new PresenceController(new Host(calls), new Surface(calls) { FailClose = true }, diagnostics);
        await controller.RequestParkAsync();
        Assert.True((await controller.RequestRestoreAsync()).IsSuccess);
        Assert.Equal(DesktopPresenceState.MainVisible, controller.State);
        Assert.Contains("restore-main", calls); Assert.Equal(1, diagnostics.Count);
    }

    [Fact]
    public async Task Entry_failure_keeps_main_visible_and_reports_diagnostic()
    {
        var calls = new List<string>(); var diagnostics = new Diagnostics();
        var controller = new PresenceController(new Host(calls), new Surface(calls) { FailEdge = true }, diagnostics);
        Assert.False((await controller.RequestParkAsync()).IsSuccess);
        Assert.Equal(DesktopPresenceState.MainVisible, controller.State);
        Assert.DoesNotContain("hide-main", calls);
        Assert.Contains("restore-main", calls);
        Assert.Equal(1, diagnostics.Count);
    }

    [Theory]
    [InlineData(ExitRequestResult.Cancelled)]
    [InlineData(ExitRequestResult.Failed)]
    public async Task Rejected_exit_preserves_popup_and_can_retry(ExitRequestResult result)
    {
        var calls = new List<string>(); var host = new Host(calls) { ExitResult = result };
        var controller = new PresenceController(host, new Surface(calls), new Diagnostics());
        await controller.RequestParkAsync(); await controller.RequestPopupAsync();
        Assert.Equal(result, await controller.RequestExitAsync());
        Assert.Equal(DesktopPresenceState.PopupOpen, controller.State);
        host.ExitResult = ExitRequestResult.Accepted;
        Assert.Equal(ExitRequestResult.Accepted, await controller.RequestExitAsync());
        Assert.Equal(DesktopPresenceState.Exiting, controller.State);
        Assert.False((await controller.RequestRestoreAsync()).IsSuccess);
    }

    [Fact]
    public async Task Accepted_exit_is_single_and_stop_does_not_reenter_host()
    {
        var calls = new List<string>(); var host = new Host(calls) { ExitResult = ExitRequestResult.Accepted };
        var surface = new Surface(calls); var controller = new PresenceController(host, surface, new Diagnostics());
        var exits = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => controller.RequestExitAsync()));
        Assert.All(exits, value => Assert.Equal(ExitRequestResult.Accepted, value));
        await controller.StopAsync(); await controller.StopAsync();
        Assert.Equal(1, host.Exits); Assert.Equal(1, surface.Stops);
        Assert.False((await controller.RequestParkAsync()).IsSuccess);
    }

    [Fact]
    public async Task Concurrent_restore_waits_for_park_and_does_not_leave_hidden_process()
    {
        var calls = new List<string>(); var surface = new Surface(calls) { EdgeGate = new() };
        var controller = new PresenceController(new Host(calls), surface, new Diagnostics());
        var park = controller.RequestParkAsync(); var restore = controller.RequestRestoreAsync();
        Assert.False(restore.IsCompleted);
        surface.EdgeGate.SetResult(); await Task.WhenAll(park, restore);
        Assert.Equal(DesktopPresenceState.MainVisible, controller.State);
        Assert.True(calls.IndexOf("restore-main") > calls.IndexOf("hide-main"));
    }

    [Fact]
    public async Task Popup_refresh_requeries_and_error_can_be_dismissed()
    {
        var calls = new List<string>(); var surface = new Surface(calls);
        var controller = new PresenceController(new Host(calls), surface, new Diagnostics());
        await controller.RequestParkAsync(); await controller.RequestPopupAsync(); await controller.RequestRefreshAsync();
        Assert.Equal(2, calls.Count(call => call == "popup"));
        await controller.RequestDismissAsync(); surface.FailPopup = true;
        Assert.False((await controller.RequestPopupAsync()).IsSuccess);
        Assert.Equal(DesktopPresenceState.PopupError, controller.State);
        await controller.RequestDismissAsync();
        Assert.Equal(DesktopPresenceState.ResidentIdle, controller.State);
    }

    [Fact]
    public async Task Resident_timing_hides_entry_when_main_window_becomes_visible()
    {
        var calls = new List<string>(); var host = new Host(calls);
        var controller = new PresenceController(host, new Surface(calls), new Diagnostics());
        controller.ConfigureShowTiming([MainWindowPresentation.Hidden]);
        await DrainAsync();
        host.Appear(MainWindowPresentation.Hidden);
        await DrainAsync();
        // Parked later: only RequestParkAsync creates the edge window; timing evaluation before
        // that may ask for visibility but must not construct it.
        Assert.DoesNotContain("edge", calls);
        await controller.RequestParkAsync();
        calls.Clear();
        // While resident the entry stays regardless of the main-window presentation; only the
        // restore path re-evaluates the timing.
        host.Appear(MainWindowPresentation.Visible);
        await DrainAsync();
        Assert.DoesNotContain("hide-edge-if-shown", calls);
        await controller.RequestRestoreAsync();
        calls.Clear();
        host.Appear(MainWindowPresentation.Visible);
        await DrainAsync();
        Assert.Contains("hide-edge-if-shown", calls);
        host.Appear(MainWindowPresentation.Hidden);
        await DrainAsync();
        Assert.Contains("show-edge-if-created", calls);
    }

    [Fact]
    public async Task Minimized_timing_shows_entry_while_minimized_and_popup_opens()
    {
        var calls = new List<string>(); var host = new Host(calls) { Presentation = MainWindowPresentation.Minimized };
        var controller = new PresenceController(host, new Surface(calls), new Diagnostics());
        controller.ConfigureShowTiming([MainWindowPresentation.Minimized, MainWindowPresentation.Hidden]);
        host.Appear(MainWindowPresentation.Minimized);
        await DrainAsync();
        // Never parked: no edge window exists yet, so nothing shows until parking.
        Assert.DoesNotContain("edge", calls);
        await controller.RequestParkAsync();
        calls.Clear();
        Assert.True((await controller.RequestPopupAsync()).IsSuccess);
        Assert.Equal(DesktopPresenceState.PopupOpen, controller.State);
        Assert.Contains("popup", calls);
    }

    [Fact]
    public async Task Default_timing_blocks_popup_while_main_window_is_visible()
    {
        var calls = new List<string>(); var host = new Host(calls);
        var controller = new PresenceController(host, new Surface(calls), new Diagnostics());
        controller.ConfigureShowTiming([MainWindowPresentation.Hidden]);
        await controller.RequestParkAsync();
        await controller.RequestRestoreAsync();
        calls.Clear();
        Assert.True((await controller.RequestPopupAsync()).IsSuccess);
        Assert.DoesNotContain("popup", calls);
        Assert.Equal(DesktopPresenceState.MainVisible, controller.State);
    }

    private static async Task DrainAsync()
    {
        for (var i = 0; i < 5; i++) await Task.Yield();
        await Task.Delay(20);
    }

    [Theory]
    [InlineData(EdgeActivationMode.SingleClick, 1, false, true)]
    [InlineData(EdgeActivationMode.SingleClick, 2, false, false)]
    [InlineData(EdgeActivationMode.DoubleClick, 1, false, false)]
    [InlineData(EdgeActivationMode.DoubleClick, 2, false, true)]
    [InlineData(EdgeActivationMode.Hover, 1, false, false)]
    [InlineData(EdgeActivationMode.Hover, 0, true, true)]
    public void Activation_modes_are_exclusive(EdgeActivationMode mode, int clicks, bool hover, bool expected) =>
        Assert.Equal(expected, PresenceActivationRules.Matches(mode, clicks, hover));
}
