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
    private long _generation;
    private bool _visible,_disposed;
    private int _x,_y;

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
        lock(_gate)
        {
            if(generation!=_generation)return;
            if(_disposed){Forms.Application.ExitThread();return;}
            if(_visible)_window!.ShowAt(_x,_y);else _window!.Hide();
        }
    }
    private void Run()
    {
        try
        {
            using var dispatcher=new Forms.Control();
            _=dispatcher.Handle;
            using var window=new ParkingWindow();
            lock(_gate)
            {
                if(_disposed)return;
                _dispatcher=dispatcher;_window=window;PostLocked();
            }
            Forms.Application.Run();
        }
        catch(Exception ex){_log($"光标停放窗口失败：{ex.Message}");}
        finally{lock(_gate){_dispatcher=null;_window=null;}}
    }
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;PostLocked();}}

    private sealed class ParkingWindow : Forms.NativeWindow,IDisposable
    {
        internal ParkingWindow()
        {
            CreateHandle(new Forms.CreateParams{Caption="NekoBridge cursor parking",Style=unchecked((int)0x80000000),ExStyle=0x08080088,Width=3,Height=3});
            if(!SetLayeredWindowAttributes(Handle,0,1,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void ShowAt(int x,int y)
        {
            if(!SetWindowPos(Handle,new nint(-1),x-1,y,3,3,0x0050))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SetCursor(0);
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
            if(m.Msg==0x14){PatBlt(m.WParam,0,0,3,3,0x00000042);m.Result=1;return;}
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
