using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.FloatingWindows.Core;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
namespace Mosaic.FloatingWindows.Wpf;

internal sealed class FloatingSurface : IFloatingWindowSurface,IDisposable
{
    private readonly IFloatingContentFactory _content;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Func<Task> _restore;
    private readonly Dictionary<FloatingContentReference,Entry> _windows=[];
    private IReadOnlyDictionary<string,object?> _values=new Dictionary<string,object?>();
    private Window? _previewWindow;
    private FloatingContentReference? _previewReference;
    private const double PreviewHeight=76;
    internal FloatingWindowCoordinator Coordinator {get;set;}=null!;
    internal FloatingSurface(IFloatingContentFactory content,IUiDispatcher dispatcher,IDiagnosticSink diagnostics,Func<Task> restore)
    {
        _content=content;_dispatcher=dispatcher;_diagnostics=diagnostics;_restore=restore;
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;SystemParameters.StaticPropertyChanged+=SystemChanged;MosaicLocalizationManager.LanguageChanged+=DisplayChanged;
    }
    private sealed class Entry(Window window,IFloatingContentSession session,FloatingWindowRecord record,TextBlock error,Button retry)
    {
        internal Window Window=window;
        internal IFloatingContentSession Session=session;
        internal FloatingWindowRecord Record=record;
        internal FloatingWindowRecord? Pending;
        internal DispatcherTimer Timer=new();
        internal TextBlock Error=error;
        internal Button Retry=retry;
        internal bool Destroying,Shown,Applying,Moving,Resized;
        internal double StartWidth,StartHeight;
        internal Button? Pin;
        internal Border Frame=null!;
        internal StackPanel Tools=null!;
        internal FloatingWindowInput Input=null!;
        internal StackPanel Footer=null!;
        internal EventHandler? LayoutHandler,MissingHandler,ModeHandler;
    }
    private int Number(string id,int fallback)=>FloatingSettings.Read(_values,id,fallback);
    private bool Flag(string id,bool fallback)=>_values.TryGetValue(id,out var value)&&value is bool flag?flag:fallback;
    internal void UpdateSettings(IReadOnlyDictionary<string,object?> values)
    {
        _values=new Dictionary<string,object?>(values);
        foreach(var entry in _windows.Values)Layout(entry);
    }
    public FloatingWindowRecord CreateRecord(FloatingContentReference reference,FloatingPlacementHint? hint)
    {
        var point=hint is null?(Point?)null:new Point(hint.ScreenX,hint.ScreenY);
        var screen=FloatingScreen.Resolve(screenPoint:point);
        if(point is {} physical && !screen.Pixels.Contains(physical))throw new ArgumentException("释放点不在可用屏幕工作区。");
        return new(reference,true,FloatingDisplayMode.Compact,Number("floating.defaultCompactWidth",320),Number("floating.defaultExpandedWidth",320),Number("floating.defaultExpandedHeight",360),screen.Id,
            point is {} p?(p.X-screen.Pixels.Left)/screen.Scale-hint!.GrabX:24,point is {} q?(q.Y-screen.Pixels.Top)/screen.Scale-hint!.GrabY:24,Flag("floating.defaultTopmost",true));
    }
    public void BeginPreview(FloatingContentReference reference,FloatingPlacementHint hint)
    {
        if(!_dispatcher.CheckAccess()){_=_dispatcher.InvokeAsync(()=>BeginPreview(reference,hint));return;}
        EndPreview();
        // An already-open task can only be activated by a detach; a ghost would promise a new position.
        if(_windows.ContainsKey(reference))return;
        _previewReference=reference;
        _previewWindow=CreatePreviewWindow();
        _previewWindow.Show();
        UpdatePreview(hint);
    }
    public void UpdatePreview(FloatingPlacementHint hint)
    {
        if(_previewWindow is null || _previewReference is null)return;
        if(!_dispatcher.CheckAccess()){_=_dispatcher.InvokeAsync(()=>UpdatePreview(hint));return;}
        try
        {
            var record=CreateRecord(_previewReference,hint);
            var screen=FloatingScreen.Resolve(record.MonitorId);
            var rect=FloatingScreen.Placement(screen,record,Number("floating.minWidth",220),PreviewHeight);
            _previewWindow.Left=rect.Left;_previewWindow.Top=rect.Top;
            _previewWindow.Width=rect.Width;_previewWindow.Height=rect.Height;
            _previewWindow.Opacity=1;
        }
        catch(ArgumentException) { _previewWindow.Opacity=0; } // Release point outside every work area.
    }
    public void EndPreview()
    {
        if(_previewWindow is null)return;
        if(!_dispatcher.CheckAccess()){_=_dispatcher.InvokeAsync(EndPreview);return;}
        var window=_previewWindow;_previewWindow=null;_previewReference=null;
        window.Close();
    }
    private Window CreatePreviewWindow()
    {
        var outline=new Rectangle{StrokeThickness=1.5,StrokeDashArray=new DoubleCollection{4,2.5},RadiusX=12,RadiusY=12,Opacity=0.8};
        outline.SetResourceReference(Shape.StrokeProperty,MosaicTheme.AccentBrushKey);
        outline.SetResourceReference(Shape.FillProperty,MosaicTheme.AccentSoftBrushKey);
        var window=new Window{WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,
            ShowActivated=false,IsHitTestVisible=false,Focusable=false,ResizeMode=ResizeMode.NoResize,Topmost=true,Content=outline};
        System.Windows.Automation.AutomationProperties.SetAutomationId(window,"FloatingPlacementPreview");
        window.SourceInitialized+=(_,_)=>
        {
            var handle=new WindowInteropHelper(window).Handle;
            var style=GetWindowLongPtr(handle,-20);
            SetWindowLongPtr(handle,-20,(nint)((long)style|0x20L|0x08000000L|0x80L));
        };
        return window;
    }
    public async Task<OperationResult> ShowAsync(FloatingWindowRecord record,CancellationToken cancellationToken)
    {
        Entry? entry=null;
        await _dispatcher.InvokeAsync(()=>entry=Create(record));
        var result=await entry!.Session.InitializeAsync(cancellationToken);
        if(!result.IsSuccess)return result;
        cancellationToken.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(()=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.Session.SetExpanded(record.Mode==FloatingDisplayMode.Expanded);Layout(entry);
            entry.Window.Show();entry.Shown=true;Layout(entry);
        });
        return OperationResult.Success();
    }
    private Entry Create(FloatingWindowRecord record)
    {
        var session=_content.Create(record.Reference);
        var window=new Window {WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.CanResize,AllowsTransparency=true,
            Background=System.Windows.Media.Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,UseLayoutRounding=true,Title="Mosaic",MinWidth=180,MinHeight=60};
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Mosaic.FloatingWindows.Wpf;component/Themes/Floating.xaml", UriKind.Relative)
        });
        window.SetResourceReference(Control.ForegroundProperty,MosaicTheme.TextPrimaryBrushKey);
        System.Windows.Automation.AutomationProperties.SetAutomationId(window,"FloatingWindow");
        var frame=new Border{CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Padding=new Thickness(Number("floating.cardPadding",8)),Margin=new Thickness(4)};
        frame.SetResourceReference(Border.BackgroundProperty,MosaicTheme.SurfaceBrushKey);frame.SetResourceReference(Border.BorderBrushProperty,MosaicTheme.BorderBrushKey);
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var bar=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};
        Button Button(string key,string text,string id)
        {
            var button=new Button {Content=text,Padding=new Thickness(4,0,4,0),MinWidth=28,MinHeight=28};button.SetResourceReference(FrameworkElement.StyleProperty,"FloatingToolButtonStyle");
            button.SetResourceReference(FrameworkElement.ToolTipProperty,key);button.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,key);System.Windows.Automation.AutomationProperties.SetAutomationId(button,id);bar.Children.Add(button);return button;
        }
        var restore=Button("Floating.Restore","↗","FloatingRestore");
        var pin=Button("Floating.Pin","◇","FloatingPin");
        var close=Button("Floating.Close","×","FloatingClose");
        grid.Children.Add(session.View);grid.Children.Add(bar);
        var footer=new StackPanel{Visibility=Visibility.Collapsed};var error=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=12};error.SetResourceReference(TextBlock.ForegroundProperty,MosaicTheme.TextSecondaryBrushKey);
        var retry=new Button{Visibility=Visibility.Collapsed,HorizontalAlignment=HorizontalAlignment.Left};retry.SetResourceReference(ContentControl.ContentProperty,"Floating.Retry");footer.Children.Add(error);footer.Children.Add(retry);Grid.SetRow(footer,1);grid.Children.Add(footer);
        frame.Child=grid;window.Content=frame;
        var entry=new Entry(window,session,record,error,retry){Pin=pin,Frame=frame,Tools=bar,Footer=footer};
        entry.Input=new FloatingWindowInput(window,frame,bar,session.Header,()=>Number("floating.dragBandHeight",12));
        _windows.Add(record.Reference,entry);
        entry.Timer.Tick+=async(_,_)=>{entry.Timer.Stop();await SaveBoundsAsync(entry);};
        close.Click+=async(_,_)=>await CloseUserAsync(entry);
        pin.Click+=async(_,_)=>await ChangeAsync(entry,r=>r with {IsTopmost=!r.IsTopmost});
        restore.Click+=async(_,_)=>{try{await _restore();}catch(Exception ex){Report(ex);Feedback(entry,OperationResult.Failure(new(ErrorCodes.InternalError,ex.Message)));}};
        retry.Click+=async(_,_)=>{if(entry.Pending is not null)await SaveBoundsAsync(entry);else await ChangeAsync(entry,r=>r);};
        window.Closing+=async(_,e)=>{if(entry.Destroying)return;e.Cancel=true;await CloseUserAsync(entry);};
        window.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Escape || entry.Record.Mode != FloatingDisplayMode.Expanded) return;
            e.Handled = true;
            if (!session.HandleEscape()) await ChangeAsync(entry, record => record with { Mode = FloatingDisplayMode.Compact });
        };
        window.SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((nint hwnd,int message,nint wp,nint lp,ref bool handled)=>{if(message==0x0231){entry.Moving=true;entry.Resized=false;entry.StartWidth=window.ActualWidth;entry.StartHeight=window.ActualHeight;}
            if(message==0x0214)entry.Resized=true;
            if(message==0x001F || message==0x0215)entry.Moving=false;
            if(message==0x0232){entry.Moving=false;var widthChanged=entry.Resized&&Math.Abs(window.ActualWidth-entry.StartWidth)>0.5;var heightChanged=entry.Resized&&Math.Abs(window.ActualHeight-entry.StartHeight)>0.5;window.Dispatcher.BeginInvoke(new Action(()=>QueueBounds(entry,widthChanged,heightChanged)));}return 0;});
        window.DpiChanged+=(_,_)=>window.Dispatcher.BeginInvoke(new Action(()=>{if(!entry.Destroying&&!entry.Moving)Layout(entry);}));
        entry.LayoutHandler=(_,_)=> {if(entry.Destroying||entry.Applying)return;window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(!entry.Destroying&&!entry.Moving)Layout(entry);}));};
        entry.ModeHandler=async(_,_)=>await ChangeAsync(entry,r=>r with {Mode=r.Mode==FloatingDisplayMode.Compact?FloatingDisplayMode.Expanded:FloatingDisplayMode.Compact});
        entry.MissingHandler = (_, _) =>
        {
            if (entry.Shown && !entry.Destroying)
                _ = window.Dispatcher.BeginInvoke(new Action(() => _ = CloseUserAsync(entry)));
        };
        session.LayoutInvalidated+=entry.LayoutHandler;session.ModeRequested+=entry.ModeHandler;session.Missing+=entry.MissingHandler;
        pin.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,"Floating.Pin");
        return entry;
    }
    private void Layout(Entry entry)
    {
        if(entry.Destroying)return;
        entry.Applying=true;
        try
        {
            entry.Window.Topmost=entry.Record.IsTopmost;
            entry.Frame.Padding=new Thickness(Number("floating.cardPadding",8));
            entry.Tools.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            entry.Session.ConfigureLayout(new(Number("floating.contentGap",4),entry.Tools.DesiredSize.Width+Number("floating.contentGap",4)));
            if(entry.Pin is {} pin)pin.Content=entry.Record.IsTopmost?"◆":"◇";
            FloatingScreen.Resolve(entry.Record.MonitorId).Place(entry.Window,entry.Record,Number("floating.minWidth",220),Number("floating.minExpandedHeight",180));
        }
        finally{entry.Applying=false;}
    }
    private void QueueBounds(Entry entry,bool widthChanged=false,bool heightChanged=false)
    {
        if(entry.Destroying || !entry.Shown || entry.Applying || entry.Window.WindowState!=WindowState.Normal)return;
        var (screen,relative)=FloatingScreen.Locate(entry.Window);
        var record=entry.Record with {MonitorId=screen.Id,X=relative.X,Y=relative.Y};
        // Only an explicit user move/resize reaches here. Preview/display clamps never overwrite preferences.
        if(record.Mode==FloatingDisplayMode.Compact){if(widthChanged)record=record with {CompactWidth=entry.Window.ActualWidth};}
        else record=record with {ExpandedWidth=widthChanged?entry.Window.ActualWidth:record.ExpandedWidth,ExpandedHeight=heightChanged?entry.Window.ActualHeight:record.ExpandedHeight};
        entry.Pending=record;entry.Timer.Stop();entry.Timer.Interval=TimeSpan.FromMilliseconds(Number("floating.boundsSaveDelayMilliseconds",300));entry.Timer.Start();
    }
    private static FloatingWindowRecord Merge(FloatingWindowRecord current,FloatingWindowRecord pending)=>current with {MonitorId=pending.MonitorId,X=pending.X,Y=pending.Y,CompactWidth=pending.CompactWidth,ExpandedWidth=pending.ExpandedWidth,ExpandedHeight=pending.ExpandedHeight};
    public FloatingWindowRecord Snapshot(FloatingWindowRecord record)=>_windows.TryGetValue(record.Reference,out var entry)&&entry.Pending is {} pending?Merge(record,pending):record;
    private async Task SaveBoundsAsync(Entry entry)
    {
        if(entry.Pending is not {} pending || entry.Destroying)return;
        var result=await Coordinator.ChangeAsync(entry.Record.Reference,current=>Merge(current,pending));
        if(result.IsSuccess && ReferenceEquals(entry.Pending,pending))entry.Pending=null;
        Feedback(entry,result);
    }
    private async Task ChangeAsync(Entry entry,Func<FloatingWindowRecord,FloatingWindowRecord> change)
    {
        try{var result=await Coordinator.ChangeAsync(entry.Record.Reference,current=>change(Snapshot(current)));Feedback(entry,result);}
        catch(Exception ex){Report(ex);Feedback(entry,OperationResult.Failure(new(ErrorCodes.InternalError,"窗口操作失败，请重试。")));}
    }
    private async Task CloseUserAsync(Entry entry)
    {
        if (entry.Destroying) return;
        try
        {
            var result = await Coordinator.CloseAsync(entry.Record.Reference);
            if (!result.IsSuccess && result.Error!.Code != ErrorCodes.Canceled) Feedback(entry, result);
        }
        catch(Exception ex){Report(ex);Feedback(entry,OperationResult.Failure(new(ErrorCodes.StorageUnavailable,"无法保存关闭状态，请重试。")));}
    }
    private static void Feedback(Entry entry,OperationResult result)
    {
        if(entry.Destroying)return;entry.Footer.Visibility=result.IsSuccess?Visibility.Collapsed:Visibility.Visible;entry.Error.Text=result.IsSuccess?"":FloatingLocalization.Text("Floating.SaveError","布局尚未保存，请重试")+" · "+result.Error!.Message;entry.Retry.Visibility=result.IsSuccess?Visibility.Collapsed:Visibility.Visible;
    }
    public void Apply(FloatingWindowRecord record)
    {
        if(!_windows.TryGetValue(record.Reference,out var entry))return;
        entry.Record=record;entry.Applying=true;try{entry.Session.SetExpanded(record.Mode==FloatingDisplayMode.Expanded);}finally{entry.Applying=false;}
        Layout(entry);
    }
    public void Activate(FloatingContentReference reference)
    {
        if(_windows.TryGetValue(reference,out var entry)){if(entry.Window.WindowState==WindowState.Minimized)entry.Window.WindowState=WindowState.Normal;entry.Window.Show();entry.Window.Activate();}
    }
    public async Task<OperationResult> PrepareCloseAsync(FloatingContentReference reference, CancellationToken cancellationToken)
    {
        Task<OperationResult>? pending = null;
        void Prepare()
        {
            if (_windows.TryGetValue(reference, out var entry))
                pending = entry.Session.PrepareCloseAsync(cancellationToken);
        }
        if (_dispatcher.CheckAccess()) Prepare();
        else await _dispatcher.InvokeAsync(Prepare);
        return pending is null ? OperationResult.Success() : await pending;
    }
    public Task CancelClosePreparationAsync(FloatingContentReference reference)
    {
        void Cancel()
        {
            if (_windows.TryGetValue(reference, out var entry)) entry.Session.CancelClosePreparation();
        }
        if (!_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(Cancel);
        Cancel();
        return Task.CompletedTask;
    }
    public async Task DestroyAsync(FloatingContentReference reference)
    {
        if(!_windows.Remove(reference,out var entry))return;
        await _dispatcher.InvokeAsync(()=>{entry.Destroying=true;entry.Input.Dispose();entry.Moving=false;entry.Timer.Stop();entry.Session.LayoutInvalidated-=entry.LayoutHandler;entry.Session.Missing-=entry.MissingHandler;entry.Session.ModeRequested-=entry.ModeHandler;entry.Window.Close();entry.Window.Content=null;});
        await entry.Session.DisposeAsync();
    }
    internal void Freeze(){EndPreview();foreach(var entry in _windows.Values){entry.Input.CancelHint();entry.Moving=false;entry.Window.IsEnabled=false;entry.Timer.Stop();}}
    internal async Task FlushAsync()
    {
        Freeze();
        foreach(var entry in _windows.Values.ToArray())
        {
            await entry.Session.FlushAsync();
        }
    }
    internal void Enable()
    {
        foreach (var entry in _windows.Values)
        {
            entry.Window.IsEnabled = true;
            if (entry.Pending is not null) entry.Timer.Start();
        }
    }
    private void DisplayChanged(object? sender,EventArgs e)=>_ = _dispatcher.InvokeAsync(()=>{foreach(var entry in _windows.Values.ToArray())Layout(entry);});
    private void SystemChanged(object? sender,PropertyChangedEventArgs e)=>DisplayChanged(sender,e);
    private void Report(Exception ex){try{_diagnostics.Report(Guid.NewGuid().ToString("N"),ex);}catch{}}
    public void Dispose(){EndPreview();SystemEvents.DisplaySettingsChanged-=DisplayChanged;SystemParameters.StaticPropertyChanged-=SystemChanged;MosaicLocalizationManager.LanguageChanged-=DisplayChanged;}
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint window,int index,nint value);
}
