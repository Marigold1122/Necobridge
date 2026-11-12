using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms=System.Windows.Forms;
using NekoBridge.Windows.DragDrop;
using NekoBridge.Windows.Input;

internal static class CaptureSmoke
{
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativeMethods.Point point);
    internal static Task Check(NativeDragBridge bridge,string file,bool edge=false)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var screen=Forms.Screen.PrimaryScreen!.Bounds;
                var cursor=new NativeMethods.Point{X=screen.Left+screen.Width/2,Y=edge?screen.Top+2:screen.Top+screen.Height/2};
                using var window=new Forms.Form{FormBorderStyle=Forms.FormBorderStyle.None,ShowInTaskbar=false,TopMost=true,StartPosition=Forms.FormStartPosition.Manual,Bounds=new Rectangle(cursor.X-24,cursor.Y-24,48,48)};
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var cleanup=timeout.Token.Register(()=>
                {
                    NativeMethods.InjectMouse(0,0,4);
                    done.TrySetException(new TimeoutException("Source helper never completed native capture"));
                    try{window.BeginInvoke(new Action(window.Close));}catch(InvalidOperationException){}
                });
                int cancel=0;
                Task? capture=null;
                using var timer=new Forms.Timer{Interval=150};
                timer.Tick+=(_,_)=>
                {
                    timer.Stop();
                    capture=Task.Run(async()=>
                    {
                        try
                        {
                            NativeMethods.GetCursorPos(out var position);
                            var paths=await bridge.CaptureAtEdgeAsync(position.X,position.Y,timeout.Token);
                            if(paths.Count!=1||paths[0]!=file)throw new IOException("Captured wrong file");
                        }
                        finally{Interlocked.Exchange(ref cancel,1);NativeMethods.InjectMouse(0,0,4);}
                    });
                };
                window.QueryContinueDrag+=(_,e)=>{if(Volatile.Read(ref cancel)!=0||timeout.IsCancellationRequested)e.Action=Forms.DragAction.Cancel;};
                window.MouseDown+=(_,args)=>
                {
                    if(args.Button!=Forms.MouseButtons.Left)return;
                    try
                    {
                        var data=new Forms.DataObject();data.SetData(Forms.DataFormats.FileDrop,new[]{file});
                        timer.Start();
                        // OLE's initial input wait needs a real coordinate change, not a posted message.
                        using var move=new System.Threading.Timer(_=>NativeMethods.MoveCursor(cursor.X+2,cursor.Y),null,100,Timeout.Infinite);
                        window.DoDragDrop(data,Forms.DragDropEffects.Copy);
                        if(capture==null)throw new IOException("Source drag ended before capture");
                        capture.GetAwaiter().GetResult();
                    }
                    catch(Exception ex){done.TrySetException(ex);}
                    finally{NativeMethods.InjectMouse(0,0,4);window.Close();}
                };
                using var click=new Forms.Timer{Interval=100};
                click.Tick+=(_,_)=>
                {
                    click.Stop();
                    NativeMethods.GetCursorPos(out var actual);
                    if(WindowFromPoint(actual)!=window.Handle)
                    {
                        done.TrySetException(new IOException("Source helper does not own cursor hit-test; refusing click"));window.Close();return;
                    }
                    NativeMethods.InjectMouse(0,0,2);
                };
                window.Shown+=(_,_)=>{NativeMethods.MoveCursor(cursor.X,cursor.Y);NativeMethods.InjectMouse(0,0,4);click.Start();};
                Forms.Application.Run(window);
                if(!done.Task.IsCompleted)Console.WriteLine(edge?"PASS native FileDrop capture at top parking edge":"PASS native FileDrop capture at screen center");
                done.TrySetResult();
            }
            catch(Exception ex){done.TrySetException(ex);}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();return done.Task;
    }
}
