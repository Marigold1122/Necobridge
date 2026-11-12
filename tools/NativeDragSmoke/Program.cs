using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NekoBridge.Core;
using NekoBridge.Windows.DragDrop;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        bool restoreCursor=Array.IndexOf(args,"--shell-only")<0;
        NekoBridge.Windows.Input.NativeMethods.GetCursorPos(out var originalCursor);
        using var watchdog=new System.Threading.Timer(_=>{if(restoreCursor){NekoBridge.Windows.Input.NativeMethods.InjectMouse(0,0,4);NekoBridge.Windows.Input.NativeMethods.MoveCursor(originalCursor.X,originalCursor.Y);}Console.Error.WriteLine("FAIL native smoke exceeded 10s; mouse released");Environment.Exit(2);},null,10000,Timeout.Infinite);
        string root=Path.Combine(Path.GetTempPath(),"NekoBridge-NativeSmoke-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{Run(root,Array.IndexOf(args,"--shell-only")>=0,Array.IndexOf(args,"--receiver-only")>=0).GetAwaiter().GetResult();Console.WriteLine("NATIVE DATA OBJECT SMOKE PASSED");return 0;}
        catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
        finally{if(restoreCursor){NekoBridge.Windows.Input.NativeMethods.InjectMouse(0,0,4);NekoBridge.Windows.Input.NativeMethods.MoveCursor(originalCursor.X,originalCursor.Y);}Directory.Delete(root,true);}
    }
    static async Task Run(string root,bool shellOnly,bool receiverOnly)
    {
        using var certA=Identity("NativeSmoke-A");using var certB=Identity("NativeSmoke-B");
        var sa=new AppSettings{DeviceName="A",Port=Port()};var sb=new AppSettings{DeviceName="B",Port=Port()};
        await using var a=new BridgeNode(sa,certA,()=>{});await using var b=new BridgeNode(sb,certB,()=>{});
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await a.StartAsync(deadline.Token);await b.StartAsync(deadline.Token);b.PairingEnabled=true;
        await a.ConnectAsync($"127.0.0.1:{sb.Port}",b.PairingKey,deadline.Token);
        string source=Path.Combine(root,"sample.txt");byte[] bytes=System.Text.Encoding.UTF8.GetBytes("native virtual file smoke\n");File.WriteAllBytes(source,bytes);
        var offer=b.CreateOffer(a.LocalId,new[]{source});
        using var cache=new RemoteFileCache(a,b.LocalId,offer.Id,Console.WriteLine);
        var data=new VirtualFileDataObject(offer,cache,Console.WriteLine);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{CheckNative(data,bytes,root);done.SetResult();}catch(Exception ex){done.SetException(ex);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task.WaitAsync(deadline.Token);
        if(!File.Exists(source)||!Convert.ToHexString(File.ReadAllBytes(source)).Equals(Convert.ToHexString(bytes)))throw new Exception("Source changed");
        string folder=Path.Combine(root,"tree");Directory.CreateDirectory(Path.Combine(folder,"nested"));Directory.CreateDirectory(Path.Combine(folder,"empty"));
        File.WriteAllBytes(Path.Combine(folder,"nested","payload.txt"),bytes);
        string zero=Path.Combine(root,"zero.txt");File.WriteAllBytes(zero,Array.Empty<byte>());
        var directoryOffer=b.CreateOffer(a.LocalId,new[]{folder,zero});
        using var directoryCache=new RemoteFileCache(a,b.LocalId,directoryOffer.Id,Console.WriteLine);
        var directoryData=new VirtualFileDataObject(directoryOffer,directoryCache,Console.WriteLine);
        var expectedTree=new Dictionary<string,byte[]?>{{"tree",null},{"tree/nested",null},{"tree/empty",null},{"tree/nested/payload.txt",bytes},{"zero.txt",Array.Empty<byte>()}};
        var directoryDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directoryThread=new Thread(()=>{try{Marshal.ThrowExceptionForHR(OleNative.OleInitialize(0));try{ShellDrop.Check(directoryData,Path.Combine(root,"shell-tree-target"),expectedTree);}finally{OleNative.OleUninitialize();}directoryDone.TrySetResult();}catch(Exception ex){directoryDone.TrySetException(ex);}}){IsBackground=true};
        directoryThread.SetApartmentState(ApartmentState.STA);directoryThread.Start();await directoryDone.Task.WaitAsync(deadline.Token);
        if(!File.ReadAllBytes(Path.Combine(folder,"nested","payload.txt")).AsSpan().SequenceEqual(bytes)||!Directory.Exists(Path.Combine(folder,"empty"))||new FileInfo(zero).Length!=0)throw new IOException("Directory source changed");
        Console.WriteLine("PASS nested directory, empty directory, multi-select and zero-byte file; sources unchanged");
        if(shellOnly)return;
        using var bridge=new NativeDragBridge(a,Console.WriteLine);
        if(!receiverOnly)
        {
        await CaptureSmoke.Check(bridge,source).WaitAsync(TimeSpan.FromSeconds(10));
        await CaptureSmoke.Check(bridge,source,true).WaitAsync(TimeSpan.FromSeconds(10));
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dragTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var operation=bridge.BeginRemoteDragAsync(b.LocalId,offer.Id,ready,commit.Task,started,dragTimeout.Token);
        try
        {
            await ready.Task.WaitAsync(dragTimeout.Token);commit.SetResult();
            await started.Task.WaitAsync(dragTimeout.Token);
            Console.WriteLine("PASS native DoDragDrop starts and emits feedback before any release");
        }
        finally{bridge.CancelRemoteDrag();try{await operation;}catch(OperationCanceledException){}}
        }
        using var router=new NekoBridge.Windows.Input.InputRouter(a,Console.WriteLine);
        await router.Ready.WaitAsync(TimeSpan.FromSeconds(2));
        router.RemoteLeftChanged+=bridge.UpdateRemoteButton;
        router.CancelDragRequested+=bridge.CancelRemoteDrag;
        await NativeDropSmoke.Check(bridge,router,b.LocalId,directoryOffer.Id,Path.Combine(root,"native-cancel-target"),expectedTree,false,true);
        await NativeDropSmoke.Check(bridge,router,b.LocalId,directoryOffer.Id,Path.Combine(root,"native-loop-left-target"),expectedTree,false);
        await NativeDropSmoke.Check(bridge,router,b.LocalId,directoryOffer.Id,Path.Combine(root,"native-loop-right-target"),expectedTree,true);
        if(!File.ReadAllBytes(Path.Combine(folder,"nested","payload.txt")).AsSpan().SequenceEqual(bytes)||!File.Exists(zero))throw new IOException("Native drop changed source files");
        Console.WriteLine("PASS native drag cancellation without dropping on user desktop");
    }
    static void CheckNative(VirtualFileDataObject data,byte[] expected,string root)
    {
        Marshal.ThrowExceptionForHR(OleNative.OleInitialize(IntPtr.Zero));
        try
        {
            nint ptr=Marshal.GetComInterfaceForObject(data,typeof(IDataObject));
            try{Guid iid=typeof(IDataObject).GUID;int hr=Marshal.QueryInterface(ptr,ref iid,out var queried);Marshal.ThrowExceptionForHR(hr);Marshal.Release(queried);}finally{Marshal.Release(ptr);}
            Console.WriteLine("PASS IDataObject native COM identity");
            var format=new FORMATETC{cfFormat=unchecked((short)OleNative.RegisterClipboardFormat("FileGroupDescriptorW")),dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
            data.GetData(ref format,out var medium);
            try{var mem=OleNative.GlobalLock(medium.unionmember);try{if(Marshal.ReadInt32(mem)!=1)throw new Exception("Descriptor count");if(Marshal.PtrToStringUni(mem+4+72)!="sample.txt")throw new Exception("Descriptor filename");}finally{OleNative.GlobalUnlock(medium.unionmember);}}finally{ReleaseStgMedium(ref medium);}
            Console.WriteLine("PASS native file descriptor layout");
            format.cfFormat=unchecked((short)OleNative.RegisterClipboardFormat("FileContents"));format.lindex=0;format.tymed=TYMED.TYMED_ISTREAM;
            data.GetData(ref format,out medium);
            try
            {
                var stream=(IStream)Marshal.GetObjectForIUnknown(medium.unionmember);stream.Stat(out var stat,1);if(stat.cbSize!=expected.Length)throw new Exception("Stream size");
                nint count=Marshal.AllocCoTaskMem(8);
                try{byte[] output=new byte[expected.Length];stream.Read(output,output.Length,count);if(Marshal.ReadInt32(count)!=output.Length||!Convert.ToHexString(output).Equals(Convert.ToHexString(expected)))throw new Exception("Native contents mismatch");}finally{Marshal.FreeCoTaskMem(count);}
            }
            finally{ReleaseStgMedium(ref medium);}
            Console.WriteLine("PASS COM IStream actual TLS file contents; source unchanged");
            ShellDrop.Check(data,Path.Combine(root,"shell-target"),new Dictionary<string,byte[]?>{{"sample.txt",expected}});
        }
        finally{OleNative.OleUninitialize();}
    }
    static int Port(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
    static X509Certificate2 Identity(string name){using var rsa=RSA.Create(2048);var req=new CertificateRequest("CN="+name,rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);using var c=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.UserKeySet);}
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
