using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Mosaic.FloatingWindows.Contracts;

namespace Mosaic.Desktop.SmokeTests;

public sealed partial class DesktopTests
{
    [Fact]
    public async Task Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds()
    {
        var root=NewRoot();var path=Path.Combine(root,"Data/mosaic.sqlite");var id=Guid.NewGuid();
        var reference=new FloatingContentReference("tasks-ui",id.ToString("D"));
        await using(var storage=new Mosaic.Storage.Sqlite.SqliteModule(new(path),new FloatingDiagnostics()))
        {
            await storage.ActivateAsync();await using var tasks=new Mosaic.Tasks.Sqlite.TasksSqliteModule(storage.Session);await tasks.ActivateAsync();
            var now=DateTimeOffset.UtcNow;Assert.True((await tasks.Repository.InsertAsync(new(id,"拖动命中验收","正文仍可选择复制",false,now,now,1))).IsSuccess);
            await using var windows=new Mosaic.FloatingWindows.Sqlite.FloatingWindowsSqliteModule(storage.Session);await windows.ActivateAsync();
            Assert.True((await windows.Repository.SaveAsync(new(reference,true,FloatingDisplayMode.Compact,320,320,360,"missing monitor",200,200,true,1),0)).IsSuccess);
        }
        var oldDpi=SetThreadDpiAwarenessContext(-4);GetCursorPos(out var originalPointer);
        Process? process=null;
        AutomationElement? Floating()=>AutomationElement.RootElement.FindFirst(TreeScope.Children,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,process!.Id),new PropertyCondition(AutomationElement.AutomationIdProperty,"FloatingWindow")));
        async Task Exit()
        {
            var main=await Until(()=>AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,process!.Id)).Cast<AutomationElement>().FirstOrDefault(e=>e.Current.Name.StartsWith("Mosaic v",StringComparison.Ordinal)),"Missing main");
            ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(0,process.ExitCode);process.Dispose();process=null;
        }
        try
        {
            process=Start(root);var floating=await Until(Floating,"Missing floating window");
            var hwnd=(nint)floating.Current.NativeWindowHandle;SetForegroundWindow(hwnd);await Task.Delay(300);
            var scale=GetDpiForWindow(hwnd)/96d;var before=ReadRect(hwnd);
            Assert.InRange((before.Bottom-before.Top)/scale,64,76);
            await PointerGesture(before.Left+(before.Right-before.Left)/2,before.Top+(int)(5*scale),60,40);
            var moved=ReadRect(hwnd);Assert.InRange(moved.Left-before.Left,58,62);Assert.InRange(moved.Top-before.Top,38,42);
            Assert.Equal(before.Right-before.Left,moved.Right-moved.Left);Assert.Equal(before.Bottom-before.Top,moved.Bottom-moved.Top);
            // Right and middle drags on the same band never move the card.
            foreach(var flags in new[]{(Down:8u,Up:16u),(Down:32u,Up:64u)})
            {await PointerGesture(moved.Left+80,moved.Top+(int)(5*scale),20,15,flags.Down,flags.Up);Assert.Equal(moved,ReadRect(hwnd));}
            // A visible top corner uses the same move path, rather than resizing.
            await PointerGesture(moved.Left+(int)(10*scale),moved.Top+(int)(7*scale),20,20);
            moved=ReadRect(hwnd);Assert.Equal(before.Right-before.Left,moved.Right-moved.Left);
            // Lower side border resizes; it must not move the origin.
            await PointerGesture(moved.Right-(int)(5*scale),moved.Top+(moved.Bottom-moved.Top)/2,32,0);
            var resized=ReadRect(hwnd);Assert.Equal(moved.Left,resized.Left);Assert.Equal(moved.Top,resized.Top);Assert.InRange((resized.Right-resized.Left)-(moved.Right-moved.Left),30,34);
            foreach(var controlId in new[]{"FloatingPin","FloatingTaskState","FloatingTaskExpand","FloatingRestore"})
            {
                SetForegroundWindow(hwnd);await Task.Delay(120);
                var control=Id(floating,controlId)!;var bounds=control.Current.BoundingRectangle;var rect=ReadRect(hwnd);
                await PointerGesture((int)(bounds.Left+bounds.Width/2),(int)(bounds.Top+bounds.Height/2),0,0);
                var after=ReadRect(hwnd);Assert.Equal(rect.Left,after.Left);Assert.Equal(rect.Top,after.Top);
            }
            SetForegroundWindow(hwnd);await Task.Delay(120);
            var expanded=ReadRect(hwnd);Assert.True(expanded.Bottom-expanded.Top>100*scale,$"Did not expand: {expanded}; scale={scale}");
            await PointerGesture(expanded.Left+100,expanded.Top+(int)(5*scale),20,20);
            var saved=ReadRect(hwnd);await Exit();
            process=Start(root);floating=await Until(Floating,"Restoration lost the window");hwnd=(nint)floating.Current.NativeWindowHandle;
            await Task.Delay(300);var restored=ReadRect(hwnd);Assert.InRange(Math.Abs(saved.Left-restored.Left),0,2);Assert.InRange(Math.Abs(saved.Top-restored.Top),0,2);
            SetForegroundWindow(hwnd);await Task.Delay(120);
            var close=Id(floating,"FloatingClose")!.Current.BoundingRectangle;
            await PointerGesture((int)(close.Left+close.Width/2),(int)(close.Top+close.Height/2),0,0);
            await UntilCondition(()=>Floating() is null,"Pointer close did not close card");await Exit();
            await using var check=new Mosaic.Storage.Sqlite.SqliteModule(new(path),new FloatingDiagnostics());await check.ActivateAsync();
            await using var taskCheck=new Mosaic.Tasks.Sqlite.TasksSqliteModule(check.Session);await taskCheck.ActivateAsync();Assert.True((await taskCheck.Repository.FindAsync(id)).Value!.IsCompleted);
        }
        finally
        {
            try { var release=new NativeInput{Mouse=new(){Flags=4}};SendInput(1,[release],Marshal.SizeOf<NativeInput>());SetCursorPos(originalPointer.X,originalPointer.Y);SetThreadDpiAwarenessContext(oldDpi); }
            finally { Cleanup(process); }
        }
    }

    private static async Task PointerGesture(int x,int y,int dx,int dy,uint down=2,uint up=4)
    {
        SetCursorPos(x,y);await Task.Delay(100);SendMouse(0,0,down);
        try{for(var i=1;i<=6;i++){await Task.Delay(35);SetCursorPos(x+dx*i/6,y+dy*i/6);}}
        finally{SendMouse(0,0,up);}
        await Task.Delay(250);
    }
    private static void SendMouse(int x,int y,uint flags)
    {
        var input=new NativeInput{Mouse=new(){X=x,Y=y,Flags=flags}};
        Assert.Equal(1u,SendInput(1,[input],Marshal.SizeOf<NativeInput>()));
    }
    private static NativeRect ReadRect(nint hwnd){Assert.True(GetWindowRect(hwnd,out var rect));return rect;}
    [StructLayout(LayoutKind.Sequential)] private record struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse { public int X,Y; public uint Data,Flags,Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit,Size=40)] private struct NativeInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public NativeMouse Mouse; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,NativeInput[] inputs,int size);
}
