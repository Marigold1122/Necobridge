using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Forms=System.Windows.Forms;
using NekoBridge.Core;
using NekoBridge.Windows.DragDrop;
using NekoBridge.Windows.Input;

internal static class NativeDropSmoke
{
    internal static async Task Check(NativeDragBridge bridge,InputRouter router,Guid peer,Guid offer,string destination,IReadOnlyDictionary<string,byte[]?> expected,bool rightEdge,bool cancel=false)
    {
        var ready=new TaskCompletionSource<(Forms.Form Window,Point Center)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var thread=new Thread(()=>
        {
            try
            {
                Marshal.ThrowExceptionForHR(OleNative.OleInitialize(0));
                try
                {
                    var bounds=Forms.Screen.PrimaryScreen!.Bounds;
                    using var window=new Forms.Form{FormBorderStyle=Forms.FormBorderStyle.None,ShowInTaskbar=false,TopMost=true,StartPosition=Forms.FormStartPosition.Manual,Bounds=new Rectangle(bounds.Width/2-100,bounds.Height/2-50,200,100)};
                    window.Shown+=(_,_)=>{window.BringToFront();Console.WriteLine($"Shell window bounds={window.Bounds} handle={window.Handle}");};
                    var shell=ShellDrop.Open(destination);
                    var proxy=new Target(shell,entered,dropped);
                    nint handle=window.Handle;
                    try
                    {
                        Marshal.ThrowExceptionForHR(RegisterDragDrop(handle,proxy));
                        window.Shown+=(_,_)=>ready.TrySetResult((window,new Point(window.Left+window.Width/2,window.Top+window.Height/2)));
                        Forms.Application.Run(window);
                    }
                    finally{RevokeDragDrop(handle);Marshal.ReleaseComObject(shell);GC.KeepAlive(proxy);}
                }
                finally{OleNative.OleUninitialize();}
                closed.TrySetResult();
            }
            catch(Exception ex){ready.TrySetException(ex);dropped.TrySetException(ex);closed.TrySetException(ex);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        var target=await ready.Task.WaitAsync(timeout.Token);
        Task? operation=null;
        try
        {
            router.Accept(peer,new ControlMessage{Kind="enter",X=rightEdge?router.Width-3:2,Y=target.Center.Y-router.Top});
            using var heartbeat=new System.Threading.Timer(_=>router.Accept(peer,new ControlMessage{Kind="keepalive"}),null,0,250);
            var prepared=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation=bridge.BeginRemoteDragAsync(peer,offer,prepared,commit.Task,started,timeout.Token);
            await prepared.Task.WaitAsync(timeout.Token);commit.TrySetResult();
            await started.Task.WaitAsync(timeout.Token);
            // Reassert the dedicated target after the IDE/tool host may take focus.
            // Never release unless the native hit-test belongs to our test window.
            var foreground=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            target.Window.BeginInvoke(new Action(()=>{target.Window.TopMost=false;target.Window.TopMost=true;target.Window.BringToFront();foreground.TrySetResult();}));
            await foreground.Task.WaitAsync(timeout.Token);
            if(router.OwnerId!=peer)throw new InvalidOperationException("Synthetic starter incorrectly revoked receiver ownership");
            router.Accept(peer,new ControlMessage{Kind="move",X=target.Center.X+3-router.Left,Y=target.Center.Y-router.Top});
            NativeMethods.GetCursorPos(out var actual);
            Console.WriteLine($"Receiver move actual=({actual.X},{actual.Y}) expected=({target.Center.X+3},{target.Center.Y}) owner={router.OwnerId==peer} targetHit={WindowFromPoint(actual)==target.Window.Handle}");
            var hit=WindowFromPoint(actual);NativeMethods.GetWindowThreadProcessId(hit,out var hitPid);
            Console.WriteLine($"Receiver hit HWND={hit} PID={hitPid} target={target.Window.Handle}");
            if(hit!=target.Window.Handle)throw new InvalidOperationException("Dedicated test target is occluded; refusing release");
            await entered.Task.WaitAsync(timeout.Token);
            if(cancel)
            {
                bridge.CancelRemoteDrag();
                try{await operation.WaitAsync(TimeSpan.FromSeconds(3));}catch(OperationCanceledException){}
                if(dropped.Task.IsCompleted)throw new InvalidOperationException("Cancelled session reached Shell Drop");
                Console.WriteLine("PASS receiver native cancellation wakes OLE and exits without Shell Drop");
                return;
            }
            router.Accept(peer,new ControlMessage{Kind="button",Data=1,Flags=1});
            await operation.WaitAsync(timeout.Token);
            await dropped.Task.WaitAsync(timeout.Token);
            ShellDrop.Verify(destination,expected);
            Console.WriteLine($"PASS receiver InputRouter.Accept enter/commit/move/buttonUp from {(rightEdge?"right":"left")} edge invokes Shell target and writes actual network files");
        }
        finally
        {
            bridge.CancelRemoteDrag();
            if(operation!=null)
            {
                try{await operation.WaitAsync(TimeSpan.FromSeconds(3));}catch(OperationCanceledException){}
            }
            NativeMethods.InjectMouse(0,0,4);
            target.Window.BeginInvoke(new Action(target.Window.Close));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    private sealed class Target(ShellDrop.IDropTarget shell,TaskCompletionSource entered,TaskCompletionSource dropped):ShellDrop.IDropTarget
    {
        public int DragEnter(IDataObject data,uint keys,ShellDrop.Point point,ref uint effect)
        {
            Console.WriteLine($"Native target DragEnter keys={keys} effect={effect}");
            if(data is IDataObjectAsyncCapability asyncData)asyncData.SetAsyncMode(false);
            int hr=shell.DragEnter(data,keys,point,ref effect);
            if(hr>=0&&effect==1)entered.TrySetResult();
            return hr;
        }
        public int DragOver(uint keys,ShellDrop.Point point,ref uint effect)=>shell.DragOver(keys,point,ref effect);
        public int DragLeave()=>shell.DragLeave();
        public int Drop(IDataObject data,uint keys,ShellDrop.Point point,ref uint effect)
        {
            try{int hr=shell.Drop(data,keys,point,ref effect);Marshal.ThrowExceptionForHR(hr);if(effect!=1)throw new InvalidOperationException("Shell rejected native copy");dropped.TrySetResult();return hr;}
            catch(Exception ex){dropped.TrySetException(ex);return Marshal.GetHRForException(ex);}
        }
    }
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativeMethods.Point point);
    [DllImport("ole32.dll")] static extern int RegisterDragDrop(nint window,[MarshalAs(UnmanagedType.Interface)]ShellDrop.IDropTarget target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(nint window);
}
