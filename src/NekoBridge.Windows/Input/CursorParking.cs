using System;
using System.Runtime.InteropServices;
using System.Threading;
using Forms = System.Windows.Forms;

namespace NekoBridge.Windows.Input;

// Never wait for this thread from a hook: SendInput can itself wait for the hook.
internal sealed class CursorParking : IDisposable
{
    private readonly object _gate=new();
    private readonly Thread _thread;
    private readonly Action<string> _log;
    private Forms.Control? _dispatcher;
    private ParkingWindow? _window;
    private Forms.Timer? _showTimer;
    private long _generation;
    private bool _visible,_disposed;
    private int _x,_y;

    // SendInput absolute-coordinate normalization may round one pixel away.
    // Keep the requested point inside a centered hit-test shield on both axes.
    internal static bool ContainsParkedPoint(int x,int y,int actualX,int actualY)
        => (long)actualX>= (long)x-4 && (long)actualX<(long)x+5
        && (long)actualY>= (long)y-4 && (long)actualY<(long)y+5;

    internal CursorParking(Action<string> log)
    {
        _log=log;
        _thread=new Thread(Run){IsBackground=true,Name="NekoBridge cursor parking"};
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    internal void Show(int x,int y){lock(_gate){if(_disposed)return;_visible=true;_x=x;_y=y;PostLocked();}}
    internal void Hide(){lock(_gate){if(_disposed)return;_visible=false;PostLocked();}}
    private void PostLocked()
    {
        long generation=++_generation;
        if(_dispatcher is {} d)
        {
            try{d.BeginInvoke(new Action(()=>Apply(generation)));}
            catch(InvalidOperationException ex){_log($"光标停放调度失败：{ex.Message}");}
        }
    }
    private void Apply(long generation)
    {
        bool visible;int x,y;
        lock(_gate)
        {
            if(generation!=_generation)return;
            if(_disposed){Forms.Application.ExitThread();return;}
            visible=_visible;x=_x;y=_y;
        }
        _showTimer!.Stop();
        if(!visible){_window!.Hide();return;}
        // Let USER32 deliver the geometric departure before putting a hider
        // over the destination. Never sleep/wait in the input hook.
        _pendingGeneration=generation;
        _showTimer.Start();
    }
    private long _pendingGeneration;
    private void ShowPending()
    {
        _showTimer!.Stop();
        int x,y;long generation=_pendingGeneration;
        lock(_gate)
        {
            if(generation!=_generation||!_visible||_disposed)return;
            x=_x;y=_y;
        }
        if(!NativeMethods.GetCursorPos(out var point)||!ContainsParkedPoint(x,y,point.X,point.Y))return;
        try{_window!.ShowAt(x,y,()=>{lock(_gate)return generation==_generation&&_visible&&!_disposed;});}
        catch(System.ComponentModel.Win32Exception ex){_window!.Hide();_log($"光标停放失败：{ex.Message}");}
    }

    private void Run()
    {
        try
        {
            using var dispatcher=new Forms.Control();
            _=dispatcher.Handle;
            using var window=new ParkingWindow();
            using var timer=new Forms.Timer{Interval=120};
            timer.Tick+=(_,_)=>ShowPending();
            lock(_gate)
            {
                if(_disposed)return;
                _dispatcher=dispatcher;_window=window;_showTimer=timer;PostLocked();
            }
            Forms.Application.Run();
        }
        catch(Exception ex){_log($"光标停放窗口失败：{ex.Message}");}
        finally{lock(_gate){_dispatcher=null;_window=null;_showTimer=null;}}
    }
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;PostLocked();}}

    internal static System.Drawing.Point ParkingPoint(System.Drawing.Rectangle screen)
    {
        System.Drawing.Rectangle? source=null;
        if(NativeMethods.GetCursorPos(out var cursor))
        {
            var hit=WindowFromPoint(cursor);
            var root=GetAncestor(hit,2); // GA_ROOT, including modern shell windows.
            if(root!=0&&GetWindowRect(root,out var r)&&r.Right>r.Left&&r.Bottom>r.Top)
                source=System.Drawing.Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
        }
        return SelectParkingPoint(screen,source);
    }
    internal static System.Drawing.Point SelectParkingPoint(System.Drawing.Rectangle screen,System.Drawing.Rectangle? source)
    {
        int insetX=Math.Min(4,(screen.Width-1)/2),insetY=Math.Min(4,(screen.Height-1)/2);
        var top=new System.Drawing.Point(screen.Left+screen.Width/2,screen.Top+insetY);
        if(source is not {} bounds||!bounds.Contains(top))return top;
        // Covering a point is not a geometric pointer exit for modern shell
        // controls. Prefer a real departure when the screen provides one.
        var bottom=new System.Drawing.Point(top.X,screen.Bottom-1-insetY);
        if(!bounds.Contains(bottom))return bottom;
        var left=new System.Drawing.Point(screen.Left+insetX,screen.Top+screen.Height/2);
        if(!bounds.Contains(left))return left;
        var right=new System.Drawing.Point(screen.Right-1-insetX,left.Y);
        return !bounds.Contains(right)?right:top;
    }
    [StructLayout(LayoutKind.Sequential)]private struct WindowRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern nint WindowFromPoint(NativeMethods.Point point);
    [DllImport("user32.dll")]private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out WindowRect rect);

    private sealed class ParkingWindow : Forms.NativeWindow,IDisposable
    {
        internal ParkingWindow()
        {
            CreateHandle(new Forms.CreateParams{Caption="NekoBridge cursor parking",Style=unchecked((int)0x80000000),ExStyle=0x08080088,Width=9,Height=9});
            if(!SetLayeredWindowAttributes(Handle,0,1,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void ShowAt(int x,int y,Func<bool> isCurrent)
        {
            if(!SetWindowPos(Handle,new nint(-1),x-4,y-4,9,9,0x0050))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // Refresh hit testing only while still inside the parked shield;
            // never pull back a cursor restored by rapid reentry/local takeover.
            if(isCurrent()&&NativeMethods.GetCursorPos(out var point)
                && ContainsParkedPoint(x,y,point.X,point.Y))
            {
                NativeMethods.InjectMouse(0,0,0x2001);
                SetCursor(0);
            }
        }
        internal void Hide()
        {
            ShowWindow(Handle,0);
            SetCursor(LoadCursor(0,new nint(32512)));
        }
        protected override void WndProc(ref Forms.Message m)
        {
            if(m.Msg==0x20){SetCursor(0);m.Result=1;return;}
            if(m.Msg==0x21){m.Result=4;return;}
            if(m.Msg==0x84){m.Result=1;return;}
            if(m.Msg==0x14){PatBlt(m.WParam,0,0,9,9,0x00000042);m.Result=1;return;}
            base.WndProc(ref m);
        }
        public void Dispose(){Hide();DestroyHandle();}
        [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(nint hwnd,uint color,byte alpha,uint flags);
        [DllImport("gdi32.dll")]private static extern bool PatBlt(nint dc,int x,int y,int width,int height,uint operation);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint LoadCursor(nint instance,nint name);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd,int command);
        [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    }
}
