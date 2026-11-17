using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Mosaic.FloatingWindows.Contracts;
namespace Mosaic.FloatingWindows.Wpf;
internal sealed record FloatingScreen(string Id,Rect Pixels,double Scale)
{
    internal double Width=>Pixels.Width/Scale;
    internal double Height=>Pixels.Height/Scale;
    internal static FloatingScreen Resolve(string? id=null,Point? screenPoint=null)
    {
        nint monitor=0;
        if(id is not null)
        {
            MonitorEnum callback=(nint candidate,nint dc,ref NativeRect rect,nint data)=> { if(Read(candidate).Id==id)monitor=candidate;return true;};
            EnumDisplayMonitors(0,0,callback,0);
        }
        if(monitor==0) monitor=screenPoint is {} point?MonitorFromPoint(new NativePoint{X=(int)point.X,Y=(int)point.Y},2):MonitorFromPoint(new NativePoint(),1);
        return Read(monitor);
    }
    private static FloatingScreen Read(nint monitor)
    {
        var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>(),Device=""};
        if(!GetMonitorInfo(monitor,ref info))return new("primary",SystemParameters.WorkArea,1);
        var dpi=96u;
        try{if(GetDpiForMonitor(monitor,0,out var x,out _)==0)dpi=x;}catch(DllNotFoundException){}
        return new(info.Device,new Rect(info.Work.Left,info.Work.Top,info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top),dpi/96d);
    }
    internal void Place(Window window,FloatingWindowRecord record,double minimumWidth,double minimumHeight)
    {
        var width=Math.Min(Width,Math.Max(minimumWidth,record.Mode==FloatingDisplayMode.Compact?record.CompactWidth:record.ExpandedWidth));
        var height=record.Mode==FloatingDisplayMode.Expanded?Math.Min(Height,Math.Max(minimumHeight,record.ExpandedHeight)):Math.Min(Height,NaturalHeight(window,width));
        window.MinWidth=Math.Min(Width,minimumWidth);window.MinHeight=Math.Min(Height,record.Mode==FloatingDisplayMode.Expanded?minimumHeight:60);
        window.Width=Math.Max(1,width);window.Height=Math.Max(1,height);
        var x=Math.Clamp(record.X,0,Math.Max(0,Width-width));var y=Math.Clamp(record.Y,0,Math.Max(0,Height-height));
        window.Left=Pixels.Left/Scale+x;window.Top=Pixels.Top/Scale+y;
        var handle=new WindowInteropHelper(window).Handle;
        if(handle!=0)SetWindowPos(handle,0,(int)Math.Round(Pixels.Left+x*Scale),(int)Math.Round(Pixels.Top+y*Scale),(int)Math.Round(width*Scale),(int)Math.Round(height*Scale),0x0014);
    }
    private static double NaturalHeight(Window window,double width)
    {
        if(window.Content is not FrameworkElement content)return 60;
        content.Measure(new Size(width,double.PositiveInfinity));return Math.Max(60,Math.Ceiling(content.DesiredSize.Height));
    }
    internal static (FloatingScreen Screen,Point Relative) Locate(Window window)
    {
        var point=window.PointToScreen(new Point());var screen=Resolve(screenPoint:point);
        return (screen,new((point.X-screen.Pixels.Left)/screen.Scale,(point.Y-screen.Pixels.Top)/screen.Scale));
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct MonitorInfo{public int Size;public NativeRect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
    private delegate bool MonitorEnum(nint monitor,nint dc,ref NativeRect rect,nint data);
    [DllImport("user32.dll")]private static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorEnum callback,nint data);
    [DllImport("user32.dll")]private static extern nint MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW",CharSet=CharSet.Unicode)]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("shcore.dll")]private static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
}
