using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Mosaic.Contracts;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.DesktopPresence.Core;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;

namespace Mosaic.DesktopPresence.Wpf;

internal sealed class PresenceSurface(IDesktopWindowHost host, ICompanionViewFactory factory, IDiagnosticSink diagnostics) : IDesktopPresenceSurface
{
    private Window? _edge;
    private Window? _popup;
    private ICompanionViewSession? _session;
    private CancellationTokenSource? _sessionCancellation;
    private readonly DispatcherTimer _hover = new();
    private readonly DispatcherTimer _dismiss = new();
    private readonly DispatcherTimer _pointer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private bool _lastPressed;
    private bool _stopped;
    private bool _closingWindows;
    private bool _observing;
    private int _sessionGeneration;
    private DispatcherOperation? _measureOperation;
    private ContextMenu? _edgeMenu;
    private EventHandler? _sessionLayoutHandler;
    private bool _toolTipActive;
    private IReadOnlyDictionary<string, object?> _values = new Dictionary<string, object?>();
    internal IDesktopPresenceApi? Api { get; set; }
    private string Text(string key) => MosaicLocalizationManager.TryFindResource(key) ?? key;
    private string Read(string id, string fallback) => PresenceSettings.ReadString(_values, id, fallback);
    private int Read(string id, int fallback) => PresenceSettings.ReadInt(_values, id, fallback);
    private bool Read(string id, bool fallback) => PresenceSettings.ReadBool(_values, id, fallback);
    private Rect Area => PresenceScreen.WorkArea(host.MainWindowHandle, Read("desktop.edge.monitor", "Primary") == "LastMainWindow");
    internal double MaximumOffset(IReadOnlyDictionary<string, object?> values)
    {
        var area = PresenceScreen.WorkArea(host.MainWindowHandle, PresenceSettings.ReadString(values, "desktop.edge.monitor", "Primary") == "LastMainWindow");
        var side = PresenceSettings.ReadString(values, "desktop.edge.anchor", "RightEdgeTop") == "RightEdgeTop" ? area.Height : area.Width;
        return Math.Max(0, side - PresenceSettings.ReadInt(values, "desktop.edge.tabSize", 32));
    }

    internal void UpdateSettings(IReadOnlyDictionary<string, object?> values)
    {
        _values = new Dictionary<string, object?>(values);
        _hover.Stop(); _dismiss.Stop();
        PositionWindows();
        if (_popup is not null && Api is { } api) _ = RefreshAsync(api);
    }

    private async Task RefreshAsync(IDesktopPresenceApi api) => ShowResult(await api.RequestRefreshAsync());

    public Task ShowEdgeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopped) throw new ObjectDisposedException(nameof(PresenceSurface));
        if (_edge is null)
        {
            _edge = CreateWindow("Presence.Edge", "PresenceEdge");
            var mark = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Focusable = true };
            mark.SetResourceReference(Border.BackgroundProperty, MosaicTheme.AccentButtonBackgroundBrushKey);
            mark.SetResourceReference(Border.BorderBrushProperty, MosaicTheme.FocusBrushKey);
            var line = new Border { Width = 3, Height = 14, Margin = new Thickness(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5) };
            line.SetResourceReference(Border.BackgroundProperty, MosaicTheme.AccentButtonTextBrushKey);
            mark.Child = line;
            var trigger = new Button { Content = mark, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                MinWidth = 0, MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, ClipToBounds = true };
            trigger.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Presence.Edge");
            System.Windows.Automation.AutomationProperties.SetAutomationId(trigger, "PresenceTrigger");
            trigger.Click += async (_, _) => await PopupAsync();
            _edge.Content = trigger;
            var menu = new ContextMenu();
            _edgeMenu = menu;
            menu.Opened += (_, _) => _dismiss.Stop();
            menu.Closed += (_, _) => ScheduleDismiss();
            menu.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; menu.IsOpen = false; trigger.Focus(); } };
            var restore = new MenuItem(); restore.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Presence.Restore");
            restore.Click += async (_, _) => await RestoreAsync();
            var exit = new MenuItem(); exit.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Presence.Exit");
            exit.Click += async (_, _) => await ExitAsync();
            menu.Items.Add(restore); menu.Items.Add(exit); trigger.ContextMenu = menu;
            _edge.MouseEnter += (_, _) =>
            {
                _dismiss.Stop();
                if (Read("desktop.edge.activation", "SingleClick") == "Hover")
                {
                    _hover.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Read("desktop.edge.hoverDelayMilliseconds", 300)));
                    _hover.Start();
                }
            };
            _edge.MouseLeave += (_, _) => { _hover.Stop(); ScheduleDismiss(); };
            _edge.PreviewMouseLeftButtonDown += async (_, e) =>
            {
                if (PresenceActivationRules.Matches(Activation, e.ClickCount)) await PopupAsync();
                e.Handled = true;
            };
            _edge.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) { e.Handled = true; await PopupAsync(); }
                else if (e.Key == Key.Escape) { e.Handled = true; await DismissAsync(); }
            };
            _edge.Closing += async (_, e) => { if (_closingWindows) return; e.Cancel = true; await RestoreAsync(); };
            _hover.Tick += HoverElapsed;
            _dismiss.Tick += DismissElapsed;
            _pointer.Tick += PointerTick;
        }
        if (!_observing)
        {
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemParameters.StaticPropertyChanged += WorkAreaChanged;
            MosaicThemeManager.ThemeChanged += DisplayChanged;
            MosaicLocalizationManager.LanguageChanged += DisplayChanged;
            _observing = true;
        }
        PositionWindows();
        _edge.Show();
        PositionWindows();
        return Task.CompletedTask;
    }

    private EdgeActivationMode Activation => Enum.TryParse<EdgeActivationMode>(Read("desktop.edge.activation", "SingleClick"), out var mode) ? mode : EdgeActivationMode.SingleClick;
    private async void HoverElapsed(object? sender, EventArgs e)
    {
        _hover.Stop();
        if (_edge?.IsMouseOver == true && PresenceActivationRules.Matches(Activation, 0, true) &&
            Api?.State == DesktopPresenceState.ResidentIdle) await PopupAsync();
    }
    private async void DismissElapsed(object? sender, EventArgs e)
    {
        _dismiss.Stop();
        if (!Inside()) await DismissAsync();
    }
    private bool Inside(bool includeFocus = true) => (includeFocus && _popup?.IsKeyboardFocusWithin == true) || _toolTipActive || _edgeMenu?.IsOpen == true ||
        (_popup?.Content is PresencePopupView view && view.IsFloatingInteractionActive) ||
        (_edge is not null && PresenceScreen.PointerInside(_edge)) || (_popup is not null && PresenceScreen.PointerInside(_popup));
    private void ScheduleDismiss()
    {
        if (_popup is null || !Read("desktop.popup.autoHide", true) || Inside()) return;
        _dismiss.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Read("desktop.popup.dismissDelayMilliseconds", 250)));
        _dismiss.Start();
    }
    private async void PointerTick(object? sender, EventArgs e)
    {
        if (_popup is null) return;
        var pressed = PresenceScreen.LeftPressed;
        var outside = !Inside();
        if (pressed && !_lastPressed && !Inside(includeFocus: false)) await DismissAsync();
        _lastPressed = pressed;
        if (!outside) _dismiss.Stop();
        else if (!_dismiss.IsEnabled) ScheduleDismiss();
    }

    public Task ShowPopupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_popup is not null || _stopped) return Task.CompletedTask;
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _session = factory.Create(new(Read("desktop.popup.filter", "All"), Read("desktop.popup.maxItems", 12),
            Read("desktop.popup.openColor", "Theme"), Read("desktop.popup.completedColor", "Theme"),
            Read("desktop.popup.rowGap", 0), Read("desktop.popup.rowPaddingY", 12),
            Read("desktop.popup.titleFontSize", 14), Read("desktop.popup.titleMaxLines", 2), Read("desktop.popup.showStatusText", true)));
        var generation = ++_sessionGeneration;
        _sessionLayoutHandler = (_, _) => QueueMeasure(generation);
        _session.LayoutInvalidated += _sessionLayoutHandler;
        _popup = CreateWindow("Presence.Title", "PresencePopup");
        var popupView = new PresencePopupView(_session.View, RestoreAsync, ExitAsync, DismissAsync,
            Read("desktop.popup.autoHide", true), Read("desktop.popup.padding", 16), Read("desktop.popup.cornerRadius", 12),
            Read("desktop.popup.showHints", false), Math.Min(Read("desktop.popup.maxHeight", 560), Area.Height) < 360);
        popupView.InteractionChanged += (_, _) => { if (Inside()) _dismiss.Stop(); else ScheduleDismiss(); };
        _popup.Content = popupView;
        _popup.AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((_, _) => { _toolTipActive = true; _dismiss.Stop(); }));
        _popup.AddHandler(ToolTipService.ToolTipClosingEvent, new ToolTipEventHandler((_, _) => { _toolTipActive = false; ScheduleDismiss(); }));
        _popup.MouseEnter += (_, _) => _dismiss.Stop();
        _popup.MouseLeave += (_, _) => ScheduleDismiss();
        _popup.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (!popupView.CloseMenuForEscape() && !(_session is ICompanionNavigation navigation && navigation.TryGoBack()))
                    await DismissAsync();
            }
        };
        _popup.Closing += async (_, e) => { if (_closingWindows) return; e.Cancel = true; await DismissAsync(); };
        PositionWindows(); _popup.Show(); PositionWindows();
        _lastPressed = PresenceScreen.LeftPressed; _pointer.Start();
        _ = InitializeSessionAsync(_session, _sessionCancellation.Token, generation);
        return Task.CompletedTask;
    }

    private async Task InitializeSessionAsync(ICompanionViewSession session, CancellationToken token, int generation)
    {
        try { await session.InitializeAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Report(exception); }
        finally { QueueMeasure(generation); }
    }

    private void QueueMeasure(int generation)
    {
        var popup = _popup;
        if (popup is null || _stopped || generation != _sessionGeneration) return;
        if (!popup.Dispatcher.CheckAccess()) { popup.Dispatcher.BeginInvoke(new Action(() => QueueMeasure(generation))); return; }
        if (_measureOperation?.Status == DispatcherOperationStatus.Pending) return;
        _measureOperation = popup.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _measureOperation = null;
            if (!_stopped && generation == _sessionGeneration && ReferenceEquals(popup, _popup)) PositionWindows();
        }));
    }

    public async Task ClosePopupAsync()
    {
        _hover.Stop(); _dismiss.Stop(); _pointer.Stop();
        ++_sessionGeneration;
        _toolTipActive = false;
        _measureOperation?.Abort(); _measureOperation = null;
        var popup = _popup; _popup = null;
        var session = _session; _session = null;
        if (session is not null && _sessionLayoutHandler is not null) session.LayoutInvalidated -= _sessionLayoutHandler;
        _sessionLayoutHandler = null;
        var cancellation = _sessionCancellation; _sessionCancellation = null;
        if (popup is not null)
        {
            if (popup.Content is PresencePopupView popupView) popupView.CloseFloatingContent();
            _closingWindows = true;
            try { popup.Close(); } finally { _closingWindows = false; }
            popup.Content = null;
        }
        try
        {
            cancellation?.Cancel();
            if (session is not null)
            {
                try { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            }
        }
        finally { cancellation?.Dispose(); }
    }

    public Task HideEdgeAsync()
    {
        _hover.Stop(); _dismiss.Stop();
        _edge?.Hide();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        Exception? closeFailure = null;
        try { await ClosePopupAsync(); } catch (Exception exception) { closeFailure = exception; }
        _hover.Stop(); _dismiss.Stop(); _pointer.Stop();
        _hover.Tick -= HoverElapsed; _dismiss.Tick -= DismissElapsed; _pointer.Tick -= PointerTick;
        if (_observing)
        {
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemParameters.StaticPropertyChanged -= WorkAreaChanged;
            MosaicThemeManager.ThemeChanged -= DisplayChanged;
            MosaicLocalizationManager.LanguageChanged -= DisplayChanged;
            _observing = false;
        }
        _closingWindows = true;
        try { if (_edgeMenu is not null) _edgeMenu.IsOpen = false; _edgeMenu = null; _edge?.Close(); _edge = null; } finally { _closingWindows = false; }
        if (closeFailure is not null) throw closeFailure;
    }

    private Window CreateWindow(string titleKey, string automationId)
    {
        var window = new Window { Title = Text(titleKey), WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, FontSize = 14, UseLayoutRounding = true };
        window.SetResourceReference(Control.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
        window.SetResourceReference(FrameworkElement.ToolTipProperty, titleKey);
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(PositionWindows);
        System.Windows.Automation.AutomationProperties.SetAutomationId(window, automationId);
        window.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, titleKey);
        return window;
    }

    private void PositionWindows()
    {
        if (_stopped) return;
        var area = Area;
        var size = Read("desktop.edge.tabSize", 32);
        var depth = Math.Min(size, Read("desktop.edge.visibleDepth", 12));
        var rightEdge = Read("desktop.edge.anchor", "RightEdgeTop") == "RightEdgeTop";
        var offset = Math.Clamp(Read("desktop.edge.offset", 24), 0, Math.Max(0, (rightEdge ? area.Height : area.Width) - size));
        if (_edge is not null)
        {
            _edge.Width = size; _edge.Height = size;
            _edge.Left = rightEdge ? area.Right - depth : area.Right - offset - size;
            _edge.Top = rightEdge ? area.Top + offset : area.Top - size + depth;
            _edge.Topmost = Read("desktop.edge.topmost", true);
            PresenceScreen.Place(_edge, host.MainWindowHandle, Read("desktop.edge.monitor", "Primary") == "LastMainWindow");
        }
        if (_popup is not null)
        {
            var width = Math.Max(1, Math.Min(Read("desktop.popup.width", 380), area.Width));
            var maximumHeight = Math.Max(1, Math.Min(Read("desktop.popup.maxHeight", 560), area.Height));
            var height = maximumHeight;
            if (Read("desktop.popup.fitContent", true) && _popup.Content is FrameworkElement content)
            {
                // Only the companion's own list scrolls. An unconstrained measure gives
                // the generic container's natural height without inspecting its content.
                content.Measure(new Size(width, double.PositiveInfinity));
                height = Math.Max(1, Math.Min(maximumHeight, Math.Ceiling(content.DesiredSize.Height)));
            }
            if (Math.Abs(_popup.Width - width) > 0.5 || double.IsNaN(_popup.Width)) _popup.Width = width;
            if (Math.Abs(_popup.Height - height) > 0.5 || double.IsNaN(_popup.Height)) _popup.Height = height;
            _popup.Left = rightEdge ? Math.Max(area.Left, area.Right - depth - _popup.Width) : Math.Clamp(area.Right - offset - _popup.Width, area.Left, area.Right - _popup.Width);
            _popup.Top = rightEdge ? Math.Clamp(area.Top + offset, area.Top, area.Bottom - _popup.Height) : area.Top + depth;
            if (_popup.Top + _popup.Height > area.Bottom) _popup.Top = area.Bottom - _popup.Height;
            _popup.Topmost = Read("desktop.edge.topmost", true);
            PresenceScreen.Place(_popup, host.MainWindowHandle, Read("desktop.edge.monitor", "Primary") == "LastMainWindow");
        }
    }
    private void DisplayChanged(object? sender, EventArgs e)
    {
        var dispatcher = _popup?.Dispatcher ?? _edge?.Dispatcher ?? Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(PositionWindows);
    }
    private void WorkAreaChanged(object? sender, PropertyChangedEventArgs e) => DisplayChanged(sender, e);
    private async Task PopupAsync() { if (Api is { } api) ShowResult(await api.RequestPopupAsync()); }
    private async Task RestoreAsync() { if (Api is { } api) ShowResult(await api.RequestRestoreAsync()); }
    private async Task DismissAsync() { if (Api is { } api) ShowResult(await api.RequestDismissAsync()); }
    private async Task ExitAsync()
    {
        if (Api is { } api && await api.RequestExitAsync() == ExitRequestResult.Failed)
            MessageBox.Show(Text("Presence.Failed"), Text("Presence.Title"));
    }
    private void ShowResult(OperationResult result) { if (!result.IsSuccess && Api?.State != DesktopPresenceState.Exiting) MessageBox.Show(result.Error!.Message, Text("Presence.Title")); }
    private void Report(Exception exception) => diagnostics.Report(Guid.NewGuid().ToString("N"), exception);
}
