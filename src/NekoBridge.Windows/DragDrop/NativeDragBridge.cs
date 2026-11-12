using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NekoBridge.Core;
using NekoBridge.Windows.Input;

namespace NekoBridge.Windows.DragDrop;

public sealed class NativeDragBridge : IDisposable
{
    private readonly BridgeNode node;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly TaskCompletionSource<HelperWindow> apartment = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WeakReference<VirtualFileDataObject>> objects = new();
    private readonly List<WeakReference<RemoteFileCache>> caches = new();
    private readonly CancellationTokenSource lifetime = new();
    private HelperWindow? capture;
    private DragSession? session;
    private bool disposed;
    private Exception? apartmentFailure;
    internal static bool SystemLeftDown => (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0;

    public NativeDragBridge(BridgeNode node, Action<string> log)
    {
        this.node = node ?? throw new ArgumentNullException(nameof(node));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        var thread = new Thread(RunApartment) { IsBackground = true, Name = "NekoBridge OLE drag" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private void RunApartment()
    {
        int hr = OleNative.OleInitialize(IntPtr.Zero);
        if (hr < 0) { apartment.TrySetException(Marshal.GetExceptionForHR(hr)!); return; }
        try
        {
            using var window = new HelperWindow();
            _ = window.Handle;
            apartment.TrySetResult(window);
            Application.Run();
        }
        catch (Exception ex) { apartment.TrySetException(ex); SafeLog("OLE 拖放线程失败：" + ex.Message); }
        finally { OleNative.OleUninitialize(); }
    }
    private void SafeLog(string message) { try { log(message); } catch { /* Logging must not unwind OLE callbacks. */ } }
    private async Task InvokeAsync(Action<HelperWindow> action)
    {
        HelperWindow window = await apartment.Task.ConfigureAwait(false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.BeginInvoke(new Action(() => { try { action(window); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
        }
        catch (Exception ex) { done.TrySetException(ex); }
        await done.Task.ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<string>> CaptureAtEdgeAsync(int x, int y, CancellationToken ct)
    {
        var result = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await InvokeAsync(_ =>
            {
                ct.ThrowIfCancellationRequested();
                CloseCapture();
                var helper = new HelperWindow { AllowDrop = true };
                capture = helper;
                bool captured = false;
                helper.DragEnter += (_, args) =>
                {
                    args.Effect = DragDropEffects.None;
                    try
                    {
                        if (captured || args.Data == null || !args.Data.GetDataPresent(DataFormats.FileDrop)) return;
                        if (args.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
                        var valid = new List<string>(paths.Length);
                        foreach (string path in paths)
                        {
                            if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
                                throw new IOException("拖放来源包含不可访问的文件路径。");
                            valid.Add(path);
                        }
                        SafeLog("拖放阶段：捕获到文件列表。");
                        captured = true;
                        result.TrySetResult(valid.AsReadOnly());
                    }
                    catch (Exception ex) { result.TrySetException(ex); }
                };
                helper.Location = new Point(x - 3, y - 3); helper.Show();
                // Force an OLE hit-test even when the pointer is pinned at the edge.
                NativeMethods.MoveCursor(x, y > SystemInformation.VirtualScreen.Top ? y - 2 : y + 2);
            }).ConfigureAwait(false);
            return await result.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { await CancelCaptureAsync().ConfigureAwait(false); }
    }
    public void CancelCapture() { _ = CancelCaptureAsync(); }
    private async Task CancelCaptureAsync() { try { await InvokeAsync(_ => CloseCapture()).ConfigureAwait(false); } catch (Exception ex) { SafeLog("关闭捕获窗口失败：" + ex.Message); } }
    private void CloseCapture() { capture?.Dispose(); capture = null; }

    public async Task BeginRemoteDragAsync(Guid sourcePeer, Guid offerId, TaskCompletionSource prepared, Task commit, TaskCompletionSource started, CancellationToken ct)
    {
        DragSession current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if(apartmentFailure!=null)throw new InvalidOperationException("原生拖放线程未能退出，请重启应用后重试。",apartmentFailure);
            if (session != null) throw new InvalidOperationException("已有远程拖放进行中。");
            // This state is installed BEFORE awaiting metadata. A release during
            // preparation must never be lost by resetting Down after the await.
            current = new DragSession(); session = current;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, current.Stop.Token);
        using CancellationTokenRegistration registration = stop.Token.Register(() => { current.Cancelled = true; Wake(current); });
        try
        {
            FileOffer offer = await node.GetOfferAsync(sourcePeer, offerId, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (!current.Down) return;
            var cache = new RemoteFileCache(node, sourcePeer, offerId, SafeLog);
            VirtualFileDataObject data;
            try { data = new VirtualFileDataObject(offer, cache, SafeLog); }
            catch { cache.Dispose(); throw; }
            lock (gate)
            {
                if (disposed) { data.Abort(); throw new ObjectDisposedException(nameof(NativeDragBridge)); }
                objects.RemoveAll(weak => !weak.TryGetTarget(out _));
                caches.RemoveAll(weak => !weak.TryGetTarget(out _));
                objects.Add(new WeakReference<VirtualFileDataObject>(data));
                caches.Add(new WeakReference<RemoteFileCache>(cache));
            }
            try
            {
                await InvokeAsync(_ => { }).WaitAsync(stop.Token).ConfigureAwait(false);
                prepared.TrySetResult();
                await commit.WaitAsync(stop.Token).ConfigureAwait(false);
                var nativeDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await InvokeAsync(window =>
                {
                    if (current.Cancelled || !current.Down) { data.Abort();nativeDone.TrySetCanceled();return; }
                    CloseCapture();
                    if(!NativeMethods.GetCursorPos(out var cursor))throw new IOException("无法取得拖放光标位置。");
                    window.Location=new Point(cursor.X-3,cursor.Y-3);
                    MouseEventHandler? begin=null;
                    begin=(_,args)=>
                    {
                        if(args.Button!=MouseButtons.Left)return;
                        SafeLog("拖放阶段：辅助窗口收到原生按下。");
                        window.MouseDown-=begin;
                        try
                        {
                            lock(current)current.Window=window.Handle;
                            current.Started=started;
                            // The helper only establishes a native mouse-down queue.
                            // Hide it before OLE hit-testing; waiting for feedback to hide
                            // leaves the first one-pixel wake inside our own source window.
                            window.Hide();
                            // OLE waits for real mouse input before its first feedback;
                            // posting WM_MOUSEMOVE to this helper does not wake its capture window.
                            using var wake=new System.Threading.Timer(_=>Wake(current),null,30,Timeout.Infinite);
                            int hr=OleNative.DoDragDrop(data,current,1,out uint effect);
                            ReleaseNativeButton(current);
                            if(current.Failure!=null)throw new IOException("原生拖放输入失败。",current.Failure);
                            if(hr<0)Marshal.ThrowExceptionForHR(hr);
                            if(current.DropRequested&&(hr!=OleNative.Drop||(effect&1)==0))
                                throw new IOException("目标程序未接受文件复制，请在可写入的文件夹内松开鼠标。");
                            SafeLog(hr==OleNative.Drop&&(effect&1)!=0?"远程复制已交给目标程序。":"远程拖放已取消。");
                            nativeDone.TrySetResult();
                        }
                        catch(Exception ex){nativeDone.TrySetException(ex);}
                        finally
                        {
                            lock(current){current.Window=IntPtr.Zero;ReleaseNativeButton(current);}
                            window.Hide();GC.KeepAlive(data);
                        }
                    };
                    window.MouseDown+=begin;
                    current.RemoveStart=()=>window.MouseDown-=begin;
                    window.Show();
                    NativeMethods.MoveCursor(cursor.X,cursor.Y);
                    var startTimer=new System.Windows.Forms.Timer{Interval=100};
                    startTimer.Tick+=(_,_)=>
                    {
                        startTimer.Stop();startTimer.Dispose();
                        if(current.Cancelled||!current.Down){nativeDone.TrySetCanceled();return;}
                        try
                        {
                            if(!NativeMethods.GetCursorPos(out var position)||WindowFromPoint(position)!=window.Handle)
                                throw new IOException("拖放辅助窗口未取得鼠标位置，请重试拖放。");
                            Interlocked.Exchange(ref current.NativeButtonDown,1);
                            NativeMethods.InjectMouse(0,0,2);
                            SafeLog("拖放阶段：已注入原生启动按下。");
                        }
                        catch(Exception ex){nativeDone.TrySetException(ex);}
                    };
                    current.RemoveStart=()=>{startTimer.Stop();startTimer.Dispose();window.MouseDown-=begin;};
                    startTimer.Start();
                }).ConfigureAwait(false);
                try{await nativeDone.Task.WaitAsync(stop.Token).ConfigureAwait(false);}
                finally
                {
                    try
                    {
                        await InvokeAsync(window=>{current.RemoveStart?.Invoke();ReleaseNativeButton(current);window.Hide();})
                            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch(TimeoutException ex)
                    {
                        current.Cancelled=true;Wake(current);
                        lock(gate)apartmentFailure=ex; // Never reuse a still-running OLE session.
                        throw new IOException("原生拖放取消超时，请重启应用后重试。",ex);
                    }
                }
                if(current.Failure!=null)throw new IOException("原生拖放输入失败。",current.Failure);
            }
            finally { if (!started.Task.IsCompletedSuccessfully) data.Abort(); }
        }
        catch (Exception ex)
        {
            prepared.TrySetException(ex);
            started.TrySetException(ex);
            throw;
        }
        finally
        {
            prepared.TrySetCanceled();
            started.TrySetCanceled();
            lock (gate) { if (ReferenceEquals(session, current)) session = null; }
            current.Stop.Dispose();
        }
    }
    public void UpdateRemoteButton(bool isDown)
    {
        lock (gate) { if (session != null && !isDown) { session.Down = false; Wake(session); } }
    }
    public void CancelRemoteDrag()
    {
        lock (gate) { if (session != null) { session.Cancelled = true; session.Stop.Cancel(); Wake(session); } }
    }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.Point point);
    private static void RecordInputFailure(DragSession current,Exception error)
    {
        Interlocked.CompareExchange(ref current.Failure,error,null);
        current.Cancelled=true;
        IntPtr window=current.Window;
        if(window!=IntPtr.Zero)OleNative.PostMessage(window,0x1F,IntPtr.Zero,IntPtr.Zero); // WM_CANCELMODE releases OLE capture without SendInput.
    }
    private static void ReleaseNativeButton(DragSession current)
    {
        if(Interlocked.Exchange(ref current.NativeButtonDown,0)==0)return;
        // Async state may already be up after InputRouter injected the remote release.
        // Our OLE thread still needs the release message for its synthetic starter.
        try{NativeMethods.InjectMouse(0,0,4);}
        catch(Exception ex){RecordInputFailure(current,ex);}
    }
    private static void Wake(DragSession current)
    {
        lock(current)
        {
            if(current.Window==IntPtr.Zero)return;
            if(current.Cancelled||!current.Down)ReleaseNativeButton(current);
            try
            {
                if(NativeMethods.GetCursorPos(out var point))
                {
                    Rectangle screen=Screen.FromPoint(new Point(point.X,point.Y)).Bounds;
                    NativeMethods.MoveCursor(point.X < screen.Right-1 ? point.X+1 : point.X-1,point.Y);
                }
            }
            catch(Exception ex){RecordInputFailure(current,ex);}
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            if (session != null) { session.Cancelled = true; Wake(session); }
            lifetime.Cancel();
            foreach (WeakReference<VirtualFileDataObject> weak in objects) if (weak.TryGetTarget(out VirtualFileDataObject? data)) data.Abort();
            foreach (WeakReference<RemoteFileCache> weak in caches) if (weak.TryGetTarget(out RemoteFileCache? cache)) cache.Dispose();
            objects.Clear(); caches.Clear();
        }
        _ = ShutdownAsync();
        GC.SuppressFinalize(this);
    }
    private async Task ShutdownAsync()
    {
        try { await InvokeAsync(_ => { CloseCapture(); Application.ExitThread(); }).ConfigureAwait(false); }
        catch (Exception ex) { SafeLog("拖放线程退出失败：" + ex.Message); }
        finally { lifetime.Dispose(); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DragSession : IDropSource
    {
        internal volatile bool Down = true, Cancelled;
        internal Exception? Failure;
        internal int NativeButtonDown;
        internal IntPtr Window;
        internal TaskCompletionSource? Started;
        internal Action? RemoveStart;
        internal bool DropRequested;
        internal readonly CancellationTokenSource Stop = new();
        // Target input is logical: pressing a native button here would click the
        // destination before OLE takes capture. Only release after OLE feedback can drop.
        public int QueryContinueDrag(bool escape, uint keys)
        {
            if (escape || Cancelled) return OleNative.Cancel;
            if (Down) return OleNative.Ok;
            if (Started?.Task.IsCompletedSuccessfully != true) return OleNative.Cancel;
            DropRequested = true;
            return OleNative.Drop;
        }
        public int GiveFeedback(uint effect)
        {
            try
            {
                if (Down && !Cancelled)
                {
                    Started?.TrySetResult();
                }
                return OleNative.DefaultCursors;
            }
            catch(Exception ex){RecordInputFailure(this,ex);return OleNative.DefaultCursors;}
        }
    }
    private sealed class HelperWindow : Form
    {
        internal HelperWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; Size = new Size(8, 8);
            TopMost = true; Opacity = 0.01;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
    }
}
