global using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NekoBridge.Core;
using NekoBridge.Windows;
using NekoBridge.Windows.History;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root=Path.Combine(Path.GetTempPath(),"NekoBridge-Perf-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var key=RSA.Create(2048);
            var request=new CertificateRequest("CN=PerfOnly",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using var identity=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
            var settings=new AppSettings();
            for(int i=0;i<5;i++)settings.Peers.Add(new(){Id=Guid.NewGuid(),Name="Test"+i,Host="127.0.0.1",Port=24000+i,Width=3840,Height=2160});
            var node=new BridgeNode(settings,identity,()=>{});var peer=settings.Peers[0].Id;
            Measure("snapshot connection query",100000,()=>{if(node.Peers.Any(p=>p.Id==peer&&p.IsConnected))throw new Exception("Unexpected connection");});
            Measure("direct connection query",100000,()=>{if(node.IsConnected(peer))throw new Exception("Unexpected connection");});
            var dragBridge=new NekoBridge.Windows.DragDrop.NativeDragBridge(node,_=>{});
            var started=typeof(NekoBridge.Windows.DragDrop.NativeDragBridge).GetField("apartmentStarted",BindingFlags.NonPublic|BindingFlags.Instance)!;
            Require(!(bool)started.GetValue(dragBridge)!,"idle drag constructor does not start OLE thread");
            dragBridge.CancelRemoteDrag();dragBridge.Dispose();
            Require(!(bool)started.GetValue(dragBridge)!,"disposing unused drag bridge does not start OLE thread");
            Console.WriteLine("PASS unused drag adapter creates no OLE apartment at construction or disposal");
            Wait(node.DisposeAsync().AsTask());

            var legacyJson=new JsonSerializerOptions{PropertyNameCaseInsensitive=false,MaxDepth=24};
            foreach(var sample in new[]{
                new WireMessage{Type="message",Message=new(){Kind="move",X=0,Y=2160}},
                new WireMessage{Type="message",Message=new(){Kind="button",Data=2,Flags=0}},
                new WireMessage{Type="message",Message=new(){Kind="key",X=160,Flags=1}},
                new WireMessage{Type="message",Message=new(){Kind="keepalive"}},
                new WireMessage{Type="hello",Device=new(Guid.NewGuid(),"temporary",3840,2160),Port=24870,Pair=false,Nonce="fixture",Mode="control"},
                new WireMessage{Type="file",Length=0,Index=0},
                new WireMessage{Type="offer",Offer=new(){Id=Guid.NewGuid(),Entries=[new(){Index=0,RelativePath="zero.txt",Length=0,IsDirectory=false},new(){Index=1,RelativePath="empty",IsDirectory=true}]}},
                new WireMessage{Type="message",Message=new(){Kind="clipboard",Text="",Id=Guid.NewGuid()}}})
            {
                var compact=JsonSerializer.SerializeToUtf8Bytes(sample,Wire.Json);
                var oldReader=JsonSerializer.Deserialize<WireMessage>(compact,legacyJson)!;
                Require(JsonSerializer.Serialize(sample,legacyJson)==JsonSerializer.Serialize(oldReader,legacyJson),"legacy reader sees identical zero/null semantics");
                var verbose=JsonSerializer.SerializeToUtf8Bytes(sample,legacyJson);
                Require(JsonSerializer.Serialize(JsonSerializer.Deserialize<WireMessage>(verbose,Wire.Json),legacyJson)==JsonSerializer.Serialize(sample,legacyJson),"new reader accepts old payload");
            }
            var moveMessage=new WireMessage{Type="message",Message=new(){Kind="move",X=120,Y=240}};
            Console.WriteLine($"move JSON payload: {JsonSerializer.SerializeToUtf8Bytes(moveMessage,legacyJson).Length} -> {JsonSerializer.SerializeToUtf8Bytes(moveMessage,Wire.Json).Length} bytes");
            Measure("verbose move serialize",100000,()=>{_=JsonSerializer.SerializeToUtf8Bytes(moveMessage,legacyJson);});
            Measure("compact move serialize",100000,()=>{_=JsonSerializer.SerializeToUtf8Bytes(moveMessage,Wire.Json);});
            Console.WriteLine("PASS compact/legacy wire compatibility for zero coordinates, button DOWN, empty text/files and handshake values");
            var message=new WireMessage{Type="message",Message=new(){Kind="clipboard",Text=new string('x',4096),Id=Guid.NewGuid()}};
            var data=JsonSerializer.SerializeToUtf8Bytes(message,Wire.Json);var frame=new byte[data.Length+4];BinaryPrimitives.WriteInt32BigEndian(frame,data.Length);data.CopyTo(frame,4);
            using var input=new MemoryStream(frame,false);
            Measure("unpooled frame reference",3000,()=>{input.Position=0;var result=ReadUnpooled(input).GetAwaiter().GetResult();Require(result.Message?.Text?.Length==4096,"reference decode");});
            Measure("pooled frame read",3000,()=>{input.Position=0;var result=Wire.ReadAsync(input,CancellationToken.None).GetAwaiter().GetResult();Require(result.Message?.Text?.Length==4096,"pooled decode");});
            foreach(var bytes in new[]{new byte[]{0,0,0,0},new byte[]{127,255,255,255},frame[..15]})
            {
                using var broken=new MemoryStream(bytes);
                try{Wire.ReadAsync(broken,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Invalid frame accepted");}
                catch(InvalidDataException){}catch(EndOfStreamException){}
            }
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();input.Position=0;
                try{Wire.ReadAsync(input,cancelled.Token).GetAwaiter().GetResult();throw new Exception("Missing cancellation");}catch(OperationCanceledException){}
            }
            input.Position=0;Require(Wire.ReadAsync(input,CancellationToken.None).GetAwaiter().GetResult().Message?.Text==message.Message.Text,"valid frame after failed reads");

            var png=CreatePng(512,8192);BitmapSource oldPreview;
            using(var stream=new MemoryStream(png,false))
            {
                var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=320;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();oldPreview=bitmap;
            }
            BitmapSource preview;using(var stream=new MemoryStream(png,false))preview=HistoryThumbnail.Decode(stream);
            Console.WriteLine($"portrait thumbnail: {oldPreview.PixelWidth}x{oldPreview.PixelHeight} -> {preview.PixelWidth}x{preview.PixelHeight}; decoded pixel bytes {PixelBytes(oldPreview):N0} -> {PixelBytes(preview):N0}");
            Require(preview.PixelWidth<=320&&preview.PixelHeight<=192&&preview.IsFrozen,"thumbnail bounds and freeze");
            using var store=new HistoryStore(root,new HistoryRetention(1000));
            var imageId=Guid.NewGuid();Wait(store.AddAsync(new(imageId,"image",settings.Id,"test",Png:png)));
            // Persistent original remains full resolution, only previews shrink.
            Require(Wait(store.ReadImageAsync(imageId))!.SequenceEqual(png),"original PNG preserved");
            var readPreview=typeof(HistoryStore).GetMethod("ReadImageFileAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.MakeGenericMethod(typeof(BitmapSource));
            var storedPreview=Wait((Task<BitmapSource?>)readPreview.Invoke(store,new object[]{imageId,(Func<Stream,BitmapSource>)HistoryThumbnail.Decode,CancellationToken.None})!);
            Require(storedPreview is {IsFrozen:true}&&storedPreview.PixelHeight<=192,"file preview detached from store stream");
            for(int i=0;i<205;i++)Wait(store.AddAsync(new(Guid.NewGuid(),"text",settings.Id,"test",Text:"Temporary item "+i)));
            var shared=new ClipboardSettingsModel(settings,()=>{},store,null!,_=>Task.CompletedTask,CancellationToken.None);
            var type=typeof(HistoryStore).Assembly.GetType("NekoBridge.Windows.History.HistoryViewModel")!;
            var model=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{store,null!,shared,(Func<Task>)(()=>Task.CompletedTask),(Action<string>)(s=>throw new Exception(s)),(Func<string,bool>)(_=>true),CancellationToken.None},null)!;
            Task Invoke(string name)=>(Task)type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(model,null)!;
            var items=(System.Collections.ICollection)type.GetProperty("Items")!.GetValue(model)!;
            Wait(Invoke("RefreshAsync"));Require(items.Count==100,"initial page");
            Wait(Invoke("LoadMoreAsync"));Require(items.Count==200,"next page append");
            type.GetMethod("ReleaseVisibleItems",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(model,null);
            Require(items.Count==0,"hidden history releases all rows");
            Wait(Invoke("RefreshAsync"));Require(items.Count==100,"reopen first page");
            var pending=Invoke("RefreshAsync");type.GetMethod("ReleaseVisibleItems",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(model,null);Wait(pending);
            Require(items.Count==0,"late query cannot repopulate hidden panel");
            Require(Wait(store.SearchAsync(limit:1000)).Count==206,"release never deletes history");
            // Long-text pages used to keep the entire clipboard text alive.
            using(var longStore=new HistoryStore(Path.Combine(root,"long"),new HistoryRetention(1000)))
            {
                string longText=new string('z',200000)+"needle-at-end";
                for(int i=0;i<20;i++)Wait(longStore.AddAsync(new(Guid.NewGuid(),"text",settings.Id,"test",Text:longText+i)));
                var full=Wait(longStore.SearchPageAsync(limit:100));
                var previewMethod=typeof(HistoryStore).GetMethod("SearchPreviewPageAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
                var shortRows=Wait((Task<IReadOnlyList<HistoryEntry>>)previewMethod.Invoke(longStore,new object?[]{null,false,null,100,0,CancellationToken.None})!);
                long fullChars=full.Sum(x=>(long)(x.Text?.Length??0)),previewChars=shortRows.Sum(x=>(long)(x.Text?.Length??0));
                Require(full.Count==shortRows.Count&&previewChars<=20*256,"preview rows bounded");
                var matching=Wait((Task<IReadOnlyList<HistoryEntry>>)previewMethod.Invoke(longStore,new object?[]{"needle-at-end",false,null,100,0,CancellationToken.None})!);
                Require(matching.Count==20,"search still covers the full original text");
                Require(Wait(longStore.GetAsync(shortRows[0].Id))!.Text==full.First(x=>x.Id==shortRows[0].Id).Text,"copy lookup reads full text");
                Console.WriteLine($"20 long-text rows: retained text payload {fullChars*2:N0} -> {previewChars*2:N0} UTF-16 bytes");
            }
            var logo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/NekoBridge.Windows/Assets/logo.png"));
            if(File.Exists(logo))
            {
                BitmapImage LoadLogo(int size){using var input=File.OpenRead(logo);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;if(size>0)bitmap.DecodePixelWidth=size;bitmap.StreamSource=input;bitmap.EndInit();bitmap.Freeze();return bitmap;}
                var originalLogo=LoadLogo(0);var smallLogo=LoadLogo(96);
                Console.WriteLine($"24-DIP logo: decoded pixel bytes {PixelBytes(originalLogo):N0} -> {PixelBytes(smallLogo):N0}");
                Require(smallLogo.PixelWidth==96,"small logo resolution");
            }
            // Unchanged searches no longer enumerate all image/copy references.
            // Explicit maintenance must still recover orphaned files.
            var orphan=Path.Combine(root,"images",new string('a',64)+".png");
            File.WriteAllText(orphan,"temporary orphan fixture");
            Wait(store.SearchAsync());Require(File.Exists(orphan),"clean page skips orphan sweep");
            Wait(store.PruneAsync());Require(!File.Exists(orphan),"explicit prune still cleans orphans");
            var favorite=(Wait(store.SearchAsync(limit:1000)))[0];Wait(store.SetFavoriteAsync(favorite.Id,true));
            Wait(store.UpdateRetentionAsync(new HistoryRetention(5)));
            var retained=Wait(store.SearchAsync(limit:1000));
            Require(retained.Count==5&&retained.Any(x=>x.Id==favorite.Id&&x.IsFavorite),"batch prune preserves favorites and limit");
            Console.WriteLine("PASS change-driven file sweep, explicit orphan recovery and retention reduction");
            Console.WriteLine("PASS framing, error/cancellation recovery, bounded previews, original images, paging and hidden-history release");
            Console.WriteLine("No windows, hooks, actual clipboard, real account data, registry or network connections used.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{Directory.Delete(root,true);}
    }
    private static void Measure(string label,int count,Action action)
    {
        for(int i=0;i<100;i++)action();
        long before=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();
        for(int i=0;i<count;i++)action();timer.Stop();long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Console.WriteLine($"{label}: {count:N0} operations, {bytes:N0} allocated bytes ({bytes/(double)count:N1}/op), {timer.Elapsed.TotalMilliseconds:N1} ms");
    }
    // The old read algorithm retained here only as a reproducible benchmark.
    private static async Task<WireMessage> ReadUnpooled(Stream input)
    {
        byte[] header=new byte[4];await input.ReadExactlyAsync(header);int length=BinaryPrimitives.ReadInt32BigEndian(header);
        byte[] bytes=new byte[length];await input.ReadExactlyAsync(bytes);return JsonSerializer.Deserialize<WireMessage>(bytes,Wire.Json)!;
    }
    private static byte[] CreatePng(int width,int height)
    {
        var pixels=new byte[width*height*4];Array.Fill(pixels,(byte)255);
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
    }
    private static long PixelBytes(BitmapSource image)=>(long)image.PixelWidth*image.PixelHeight*image.Format.BitsPerPixel/8;
    private static void Wait(Task task)
    {
        if(!task.IsCompleted){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;_=task.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);Dispatcher.PushFrame(frame);}task.GetAwaiter().GetResult();
    }
    private static T Wait<T>(Task<T> task){Wait((Task)task);return task.GetAwaiter().GetResult();}
    private static void Require(bool condition,string text){if(!condition)throw new Exception(text);}
}
