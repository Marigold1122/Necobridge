using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using NekoBridge.Core;

namespace NekoBridge.Windows.DragDrop;

// Downloads run on the pool, never on the OLE apartment. Shell receives a seekable
// disk-backed IStream immediately; its first Read waits only for this file.
internal sealed class RemoteFileCache : IDisposable
{
    private readonly BridgeNode node;
    private readonly Guid peer, offer;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<int, Task<string>> downloads = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "NekoBridge", Guid.NewGuid().ToString("N"));
    private bool disposed;
    internal RemoteFileCache(BridgeNode node, Guid peer, Guid offer, Action<string> log)
    { this.node = node; this.peer = peer; this.offer = offer; this.log = log; }
    internal Task<string> Get(FileEntry entry)
    {
        lock (downloads)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!downloads.TryGetValue(entry.Index, out Task<string>? task))
            {
                task = Task.Run(() => Download(entry));
                downloads.Add(entry.Index, task);
            }
            return task;
        }
    }
    private async Task<string> Download(FileEntry entry)
    {
        string path = Path.Combine(directory, entry.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(directory);
            await using Stream remote = await node.OpenRemoteFileAsync(peer, offer, entry.Index, stop.Token).ConfigureAwait(false);
            await using (var local = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536];
                long remaining = entry.Length;
                while (remaining > 0)
                {
                    int read = await remote.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), stop.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Remote file ended before its advertised length.");
                    await local.WriteAsync(buffer.AsMemory(0, read), stop.Token).ConfigureAwait(false);
                    remaining -= read;
                }
                if (await remote.ReadAsync(buffer.AsMemory(0, 1), stop.Token).ConfigureAwait(false) != 0)
                    throw new IOException("Remote file exceeded its advertised length.");
            }
            return path;
        }
        catch (Exception ex)
        {
            log("文件拖放读取失败：" + ex.Message);
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }
    ~RemoteFileCache() { Dispose(); }
    public void Dispose()
    {
        Task<string>[] pending;
        lock (downloads) { if (disposed) return; disposed = true; stop.Cancel(); pending = new Task<string>[downloads.Count]; downloads.Values.CopyTo(pending, 0); }
        GC.SuppressFinalize(this);
        _ = Task.WhenAll(pending).ContinueWith(_ =>
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception ex) { log("拖放缓存清理失败：" + ex.Message); }
            stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class CachedComStream : IStream, IDisposable
{
    private readonly RemoteFileCache cache;
    private readonly FileEntry entry;
    private readonly object gate = new();
    private FileStream? file;
    private long position;
    private bool disposed;
    internal CachedComStream(RemoteFileCache cache, FileEntry entry, long position = 0) { this.cache = cache; this.entry = entry; this.position = position; }
    private FileStream Open()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (file == null) { file = new FileStream(cache.Get(entry).GetAwaiter().GetResult(), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); file.Position = position; }
        return file;
    }
    public void Read(byte[] buffer, int count, IntPtr read)
    {
        if (count < 0 || count > buffer.Length) throw new COMException("Invalid read size.", unchecked((int)0x80070057));
        lock (gate)
        {
            FileStream stream = Open(); int total = 0;
            while (total < count) { int n = stream.Read(buffer, total, count - total); if (n == 0) break; total += n; }
            position = stream.Position;
            if (read != IntPtr.Zero) Marshal.WriteInt32(read, total);
        }
    }
    public void Seek(long move, int origin, IntPtr result)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long basis = origin switch { 0 => 0, 1 => position, 2 => entry.Length, _ => throw new COMException("Invalid origin.", unchecked((int)0x80070057)) };
            long next = checked(basis + move);
            if (next < 0) throw new COMException("Negative seek.", unchecked((int)0x80030019));
            position = next; if (file != null) file.Position = next;
            if (result != IntPtr.Zero) Marshal.WriteInt64(result, next);
        }
    }
    public void CopyTo(IStream target, long count, IntPtr read, IntPtr written)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        long totalRead = 0, totalWritten = 0;
        byte[] buffer = new byte[65536]; IntPtr amount = Marshal.AllocCoTaskMem(8);
        try
        {
            while (totalRead < count)
            {
                Read(buffer, (int)Math.Min(buffer.Length, count - totalRead), amount);
                int n = Marshal.ReadInt32(amount); if (n == 0) break;
                totalRead += n; Marshal.WriteInt32(amount, 0); target.Write(buffer, n, amount);
                int output = Marshal.ReadInt32(amount); totalWritten += output;
                if (output != n) throw new COMException("Short destination write.", unchecked((int)0x8003001D));
            }
        }
        finally { Marshal.FreeCoTaskMem(amount); if (read != IntPtr.Zero) Marshal.WriteInt64(read, totalRead); if (written != IntPtr.Zero) Marshal.WriteInt64(written, totalWritten); }
    }
    public void Stat(out STATSTG stat, int flags)
    { stat = new STATSTG { type = 2, cbSize = entry.Length, grfMode = 0, pwcsName = (flags & 1) == 0 ? entry.RelativePath : null! }; }
    public void Clone(out IStream result) { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); result = new CachedComStream(cache, entry, position); } }
    public void Commit(int flags) { }
    public void Write(byte[] buffer, int count, IntPtr written) => throw new COMException("Read only.", unchecked((int)0x80030005));
    public void SetSize(long value) => throw new COMException("Read only.", unchecked((int)0x80030005));
    public void Revert() => throw new COMException("Not supported.", OleNative.NotImpl);
    public void LockRegion(long offset, long count, int type) => throw new COMException("Not supported.", OleNative.NotImpl);
    public void UnlockRegion(long offset, long count, int type) => throw new COMException("Not supported.", OleNative.NotImpl);
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; file?.Dispose(); } GC.SuppressFinalize(this); }
    ~CachedComStream() { Dispose(); }
}
