using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NekoBridge.Core;
using NekoBridge.Windows;
using NekoBridge.Windows.DragDrop;

internal static class RichSmoke
{
    internal static async Task Run(Dispatcher dispatcher)
    {
        string root = Path.Combine(Path.GetTempPath(), "NekoBridge-ClipboardSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var ca = Identity(); using var cb = Identity(); using var cc = Identity();
        var sa = new AppSettings { Port = Port() }; var sb = new AppSettings { Port = Port() }; var sc = new AppSettings { Port = Port() };
        await using var a = new BridgeNode(sa, ca, () => { });
        await using var b = new BridgeNode(sb, cb, () => { });
        await using var c = new BridgeNode(sc, cc, () => { });
        var nodes = new[] { a, b, c };
        var clips = nodes.Select(_ => new Fake()).ToArray();
        var sync = new ClipboardSync[3];
        var logs = new List<string>();
        int sends = 0;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var node in nodes) { await node.StartAsync(deadline.Token); node.PairingEnabled = true; }
            await a.ConnectAsync($"127.0.0.1:{sb.Port}", b.PairingKey, deadline.Token);
            await a.ConnectAsync($"127.0.0.1:{sc.Port}", c.PairingKey, deadline.Token);
            for (int i = 0; i < nodes.Length; i++)
            {
                int index = i;
                sync[i] = new ClipboardSync(() => true, logs.Add, dispatcher,
                    () => nodes[index].Peers.Where(p => p.IsConnected).Select(p => p.Id).ToArray(),
                    (peer, message, token) =>
                    {
                        sends++;
                        if ((message.Payload?.Length ?? 0) > 1024 || message.Text != null) throw new Exception("Rich content leaked into control frame");
                        sync[Array.FindIndex(nodes, n => n.LocalId == peer)].Receive(nodes[index].LocalId, message);
                        return Task.CompletedTask;
                    }, clips[index], nodes[index]);
            }
            await HistoryRegression(a, dispatcher, root);
            ImageRegression();
            byte[] pixels = { 30, 80, 140, 255, 240, 100, 20, 128 };
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream(); encoder.Save(png);
            RichClipboard image = RichClipboard.Image(png.ToArray());
            var data = image.CreateImageData();
            var com = (System.Runtime.InteropServices.ComTypes.IDataObject)data;
            foreach (short format in new short[] { 2, 8, 17 })
            {
                var request = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1,
                    tymed = format == 2 ? TYMED.TYMED_GDI : TYMED.TYMED_HGLOBAL };
                if (com.QueryGetData(ref request) != 0) throw new Exception("Native image format unavailable: " + format);
                com.GetData(ref request, out var medium); ReleaseStgMedium(ref medium);
            }
            Check(!data.GetDataPresent(System.Windows.DataFormats.FileDrop), "image is actual native bitmap, DIB and DIBV5, not file drop");
            byte[] bomb = png.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(16, 4), 100000);
            try { RichClipboard.Image(bomb); throw new Exception("Image allocation bomb accepted"); } catch (InvalidDataException) { }
            Console.WriteLine("PASS PNG dimensions rejected before decoder allocation");
            clips[1].Copy(image); sync[1].HandleClipboardUpdate();
            await Until(() => clips[2].Rich?.Fingerprint == image.Fingerprint, deadline.Token);
            Check(clips[0].Rich?.Fingerprint == image.Fingerprint && sends == 2, "right-origin PNG travels authenticated offers through hub to third peer");
            int writes = clips.Sum(x => x.Writes);
            for(int i=0;i<3;i++) { clips[i].Copy(image); sync[i].HandleClipboardUpdate(); }
            await Task.Delay(120);
            Check(sends == 2 && clips.Sum(x => x.Writes) == writes, "same pixel image rewrites do not echo or overwrite");
            string tree = Path.Combine(root, "tree"); Directory.CreateDirectory(Path.Combine(tree, "empty")); Directory.CreateDirectory(Path.Combine(tree,"nested"));
            byte[] payload = "clipboard shell contents"u8.ToArray(); File.WriteAllBytes(Path.Combine(tree,"nested","a.txt"),payload);
            var files = new RichClipboard("files", RichClipboard.Digest("test-local-files"), [tree]);
            clips[1].Copy(files); sync[1].HandleClipboardUpdate();
            await Until(() => clips[2].Rich?.Kind == "files", deadline.Token);
            RichClipboard target = clips[2].Rich!;
            Check(target.Peer == a.LocalId && target.Offer != null, "third peer receives authenticated relay offer, not source paths");
            using var cache = new RemoteFileCache(c,target.Peer,target.Offer!.Id,logs.Add);
            var virtualData = new VirtualFileDataObject(target.Offer,cache,logs.Add);
            Check(RichClipboard.ReadVirtual(virtualData)?.Fingerprint == RichClipboard.FilesFingerprint(target.Offer.Entries), "native virtual-file descriptor fingerprint suppresses manager format echoes");
            await OnSta(() => ShellDrop.Check(virtualData,Path.Combine(root,"destination"),new Dictionary<string,byte[]?>
            { ["tree"] = null, ["tree/empty"] = null, ["tree/nested"] = null, ["tree/nested/a.txt"] = payload }));
            Check(File.ReadAllBytes(Path.Combine(tree,"nested","a.txt")).SequenceEqual(payload), "native Shell COM paste-data extraction preserves sources and empty directories");
            int previousWrites = clips[2].Writes;
            var fresh = b.CreateOffer(a.LocalId, [tree]);
            var freshRelay = a.CreateRelayOffer(c.LocalId, b.LocalId, fresh);
            sync[2].Receive(a.LocalId, new ControlMessage { Kind = "clipboard", Id = Guid.NewGuid(),
                Payload = System.Text.Json.JsonSerializer.Serialize(new { Kind = "files", Offer = freshRelay.Id }) });
            await Until(() => clips[2].Writes > previousWrites, deadline.Token);
            Check(clips[2].Rich!.Offer!.Id == freshRelay.Id, "same-name same-size files with a new authenticated offer are not mistaken for equal content");
            int beforeRecopy = sends;
            File.SetLastWriteTimeUtc(Path.Combine(tree,"nested","a.txt"), DateTime.UtcNow.AddSeconds(3));
            clips[1].Copy(files); sync[1].HandleClipboardUpdate();
            await Until(() => sends >= beforeRecopy + 2, deadline.Token);
            Check(sends >= beforeRecopy + 2, "recopying identical paths after a nested same-size file modification publishes a new offer");
            await Task.Delay(100);
            Check(logs.Count == 0, "rich clipboard operations report no errors");
            Console.WriteLine("RICH CLIPBOARD SMOKE PASSED (fake clipboard only; native Shell target is a new temporary folder).");
        }
        finally
        {
            foreach (var instance in sync) instance?.Dispose();
            Directory.Delete(root,true);
        }
    }
    private static async Task HistoryRegression(BridgeNode node, Dispatcher dispatcher, string root)
    {
        string historyRoot = Path.Combine(root, "history-regression");
        string source = Path.Combine(root, "history-source.bin");
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(16 * 1024 * 1024);
        var fake = new Fake();
        using var clipboard = new ClipboardSync(() => false, _ => { }, dispatcher, () => Array.Empty<Guid>(),
            (_, _, _) => throw new Exception("Unexpected history network send"), fake, node, historyEnabled: () => true);
        Guid id = Guid.NewGuid();
        using (var store = new NekoBridge.Windows.History.HistoryStore(historyRoot))
        using (var bridge = new NekoBridge.Windows.History.HistoryBridge(clipboard, store, node, _ => { }))
        {
            var entry = await store.AddAsync(new(id, "files", node.LocalId, "Smoke", Paths: [source]));
            var saving = bridge.SaveFileCopyAsync(entry);
            await bridge.StopAsync();
            try { await saving; throw new Exception("Stopped save unexpectedly succeeded"); }
            catch (OperationCanceledException) { }
            Check(!Directory.Exists(Path.Combine(historyRoot, "copies", id.ToString("N"), "files")),
                "history shutdown waits for incomplete saved-copy cleanup");
        }
        using (var store = new NekoBridge.Windows.History.HistoryStore(historyRoot))
        using (var bridge = new NekoBridge.Windows.History.HistoryBridge(clipboard, store, node, _ => { }))
        {
            var entry = (await store.GetAsync(id))!;
            string[] saved = await bridge.SaveFileCopyAsync(entry);
            Check(saved.Length == 1 && new FileInfo(saved[0]).Length == new FileInfo(source).Length,
                "saved copy can be retried after interrupted-save shutdown and store restart");
            int before = (await store.SearchAsync()).Count;
            await bridge.RestoreAsync((await store.GetAsync(id))!);
            await bridge.StopAsync();
            Check(fake.Rich?.Paths?.SequenceEqual(saved) == true && (await store.SearchAsync()).Count == before,
                "restoring saved file paths does not create an unowned duplicate history entry");
        }
    }
    private static void ImageRegression()
    {
        void Put(byte[] data, int offset, int value) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
        byte[] Dib(int bits, bool topDown)
        {
            int stride = 4; // One 24bpp pixel plus one padding byte, or one 32bpp pixel.
            byte[] result = new byte[40 + stride * 2];
            Put(result, 0, 40); Put(result, 4, 1); Put(result, 8, topDown ? -2 : 2); Put(result, 12, bits << 16 | 1);
            byte[] rows = { 30, 80, 140, 0, 240, 100, 20, 0 };
            for (int row = 0; row < 2; row++) Array.Copy(rows, row * 4, result, 40 + (topDown ? row : 1 - row) * stride, 4);
            return result;
        }
        RichClipboard Capture(short id, byte[] bytes)
            => ClipboardImage.CaptureFromDataObject(new ImageData(id, bytes)) ?? throw new Exception("Image not captured");
        byte[] Pixels(RichClipboard image)
        {
            using var input = new MemoryStream(image.Png!, false);
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            byte[] result = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(result, converted.PixelWidth * 4, 0); return result;
        }
        byte[] expected = { 30, 80, 140, 255, 240, 100, 20, 255 };
        foreach (int bits in new[] { 24, 32 }) foreach (bool topDown in new[] { false, true })
        {
            var image = Capture(8, Dib(bits, topDown));
            Check(Pixels(image).SequenceEqual(expected), $"native COM BI_RGB {bits}bpp {(topDown ? "top-down" : "bottom-up")} colored screenshot is opaque; padded rows correct");
            var written = (System.Runtime.InteropServices.ComTypes.IDataObject)image.CreateImageData();
            Check(Pixels(ClipboardImage.CaptureFromDataObject(written)!).SequenceEqual(expected), "screenshot write/read PNG rendered pixels remain visible");
            var request = new FORMATETC { cfFormat = 17, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            written.GetData(ref request, out var medium);
            try
            {
                int size = checked((int)GlobalSize(medium.unionmember)); byte[] dib = new byte[size];
                nint pointer = GlobalLock(medium.unionmember);
                if (pointer == 0) throw new Exception("GlobalLock failed");
                try { Marshal.Copy(pointer, dib, 0, size); } finally { GlobalUnlock(medium.unionmember); }
                Check(Pixels(Capture(17, dib)).SequenceEqual(expected), "written native DIBV5 rendered pixels remain visible");
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        foreach (byte alpha in new byte[] { 0, 128 })
        {
            byte[] original = { 30, 80, 140, alpha, 240, 100, 20, alpha };
            var bitmap = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null, original, 4);
            var png = ClipboardImage.Encode(bitmap);
            short pngId = unchecked((short)RegisterClipboardFormat("PNG"));
            Check(Pixels(Capture(pngId, png.Png!)).SequenceEqual(original), $"PNG alpha {alpha} preserved exactly (including fully transparent)");
            byte[] v5 = new byte[132]; Put(v5, 0, 124); Put(v5, 4, 1); Put(v5, 8, -2); Put(v5, 12, 0x00200001); Put(v5, 16, 3);
            Put(v5, 40, 0xff0000); Put(v5, 44, 0xff00); Put(v5, 48, 0xff); Put(v5, 52, unchecked((int)0xff000000)); original.CopyTo(v5, 124);
            Check(Pixels(Capture(17, v5)).SequenceEqual(original), $"native COM DIBV5 explicit alpha {alpha} preserved exactly");
            var output = (System.Runtime.InteropServices.ComTypes.IDataObject)png.CreateImageData();
            Check(Pixels(ClipboardImage.CaptureFromDataObject(output)!).SequenceEqual(original), "transparent PNG write/read pixels preserved (PNG takes priority over native formats)");
            var v5Request = new FORMATETC { cfFormat = 17, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            output.GetData(ref v5Request, out var v5Medium);
            try
            {
                byte[] emitted = new byte[checked((int)GlobalSize(v5Medium.unionmember))];
                nint pointer = GlobalLock(v5Medium.unionmember);
                if (pointer == 0) throw new ExternalException();
                try { Marshal.Copy(pointer, emitted, 0, emitted.Length); } finally { GlobalUnlock(v5Medium.unionmember); }
                Check(Pixels(Capture(17, emitted)).SequenceEqual(original), "written DIBV5 preserves transparent and semitransparent rendered pixels");
            }
            finally { ReleaseStgMedium(ref v5Medium); }
        }
        var bad = new List<byte[]> { new byte[12], Dib(32, false)[..43] };
        byte[] huge = Dib(32, false); Put(huge, 4, int.MaxValue); bad.Add(huge);
        byte[] minHeight = Dib(32, false); Put(minHeight, 8, int.MinValue); bad.Add(minHeight);
        byte[] table = Dib(32, false); Put(table, 32, int.MaxValue); bad.Add(table);
        byte[] unsupported = Dib(32, false); Put(unsupported, 16, 1); bad.Add(unsupported);
        foreach (byte[] bytes in bad)
        {
            bool rejected = false;
            try { Capture(8, bytes); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "native COM malformed/truncated/unsupported DIB rejected before pixel allocation");
        }
    }
    // Real COM FORMATETC/STGMEDIUM path, never accesses the user's clipboard.
    private sealed class ImageData(short id, byte[] bytes) : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        public int QueryGetData(ref FORMATETC format) => format.cfFormat == id && format.tymed == TYMED.TYMED_HGLOBAL ? 0 : unchecked((int)0x80040064);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            Marshal.ThrowExceptionForHR(QueryGetData(ref format));
            nint handle = GlobalAlloc(0x42, (nuint)bytes.Length);
            if (handle == 0) throw new OutOfMemoryException();
            nint pointer = GlobalLock(handle);
            if (pointer == 0) { GlobalFree(handle); throw new ExternalException(); }
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(handle); }
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return unchecked((int)0x80040003); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    private static async Task Until(Func<bool> condition, CancellationToken token)
    { while(!condition()) { token.ThrowIfCancellationRequested(); await Task.Delay(20, token); } }
    private static Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { Marshal.ThrowExceptionForHR(OleInitialize(0)); try { action(); } finally { OleUninitialize(); } done.SetResult(); } catch(Exception ex) { done.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static X509Certificate2 Identity()
    { using var rsa=RSA.Create(2048); var request=new CertificateRequest("CN=ClipboardSmoke",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1); using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1)); return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.UserKeySet); }
    private sealed class Fake : ClipboardSync.IClipboard
    {
        public RichClipboard? Rich; public int Writes; public uint Sequence { get; private set; }
        public void Copy(RichClipboard rich) { Rich=rich;Sequence++; }
        public string? Read()=>null;
        public RichClipboard? ReadRich()=>Rich;
        public uint Write(string text)=>throw new Exception("Unexpected text write");
        public uint WriteRich(RichClipboard rich,BridgeNode node,Action<string> log) { Writes++; Copy(rich);return Sequence; }
        public void Dispose() { }
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
