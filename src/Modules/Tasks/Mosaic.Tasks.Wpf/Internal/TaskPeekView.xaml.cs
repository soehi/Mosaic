using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Mosaic.FloatingWindows.Contracts;
namespace Mosaic.Tasks.Wpf.Internal;
internal partial class TaskPeekView : UserControl
{
    internal event Action<TaskPeekRow>? OpenRequested;
    internal Func<TaskPeekRow,FloatingPlacementHint?,CancellationToken,Task>? DetachRequested;
    internal Func<bool>? CanDetach;
    internal event Action<bool>? DragChanged;
    private Button? _candidate;
    private Point _start;
    private Point _grab;
    private bool _dragging,_suppressClick;
    private Popup? _preview;
    private CancellationTokenSource? _opening;
    internal TaskPeekView() { InitializeComponent(); }
    private void OpenTask(object sender,RoutedEventArgs e)
    {
        if(_suppressClick)return;
        if(sender is Button { DataContext:TaskPeekRow row })OpenRequested?.Invoke(row);
    }
    private void StartGesture(object sender,MouseButtonEventArgs e)
    {
        CancelDrag(); _suppressClick=false;
        if(CanDetach?.Invoke()!=true || sender is not Button button)return;
        _candidate=button;_start=e.GetPosition(this);_grab=e.GetPosition(button);
    }
    private void MoveGesture(object sender,MouseEventArgs e)
    {
        if(_candidate is null || e.LeftButton!=MouseButtonState.Pressed)return;
        var point=e.GetPosition(this);
        if(!_dragging && (Math.Abs(point.X-_start.X)>SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y-_start.Y)>SystemParameters.MinimumVerticalDragDistance))
        {
            _dragging=true;_suppressClick=true;_candidate.CaptureMouse();DragChanged?.Invoke(true);
            _preview=new Popup {PlacementTarget=this,Placement=PlacementMode.Relative,AllowsTransparency=true,IsHitTestVisible=false,StaysOpen=true,
                Child=new TextBlock {Text=TaskLocalization.Localize("Tasks.Floating.Drag","松手以创建小窗 · Esc取消"),Padding=new Thickness(8),Background=System.Windows.SystemColors.InfoBrush,Foreground=System.Windows.SystemColors.InfoTextBrush}};
            _preview.IsOpen=true;
        }
        if(_dragging){e.Handled=true;if(_preview is not null){_preview.HorizontalOffset=point.X+12;_preview.VerticalOffset=point.Y+12;}}
    }
    private async void EndGesture(object sender,MouseButtonEventArgs e)
    {
        if(!_dragging || _candidate?.DataContext is not TaskPeekRow row){_candidate=null;return;}
        e.Handled=true;
        var screen=PointToScreen(e.GetPosition(this));var grab=_grab;
        var window=Window.GetWindow(this);
        var local=window?.PointFromScreen(screen);
        var outside=window is not null && local is {} point && (point.X<0||point.Y<0||point.X>window.ActualWidth||point.Y>window.ActualHeight);
        var opening=new CancellationTokenSource();_opening=opening;
        try {if(outside && DetachRequested is {} request) await request(row,new(screen.X,screen.Y,grab.X,grab.Y),opening.Token);}
        finally{if(ReferenceEquals(_opening,opening)){_opening=null;CancelDrag();}opening.Dispose();}
    }
    private void CaptureLost(object sender,MouseEventArgs e){if(_dragging)CancelDrag();}
    internal bool CancelDrag()
    {
        var opening=_opening;_opening=null;opening?.Cancel();
        var was=_dragging;_dragging=false;
        if(_preview is not null){_preview.IsOpen=false;_preview=null;}
        var button=_candidate;_candidate=null;
        if(button?.IsMouseCaptured==true)button.ReleaseMouseCapture();
        if(was)DragChanged?.Invoke(false);
        return was;
    }
    private void OpenMenu(object sender,ContextMenuEventArgs e)
    {
        if(CanDetach?.Invoke()!=true || sender is not Button {DataContext:TaskPeekRow row} button){e.Handled=true;return;}
        var menu=button.ContextMenu!;menu.Items.Clear();
        var item=new MenuItem{Header=TaskLocalization.Localize("Tasks.Floating.Open","在小窗中打开")};
        item.Click+=async(_,_)=>{if(DetachRequested is {} request)await request(row,null,CancellationToken.None);};menu.Items.Add(item);
    }
}
