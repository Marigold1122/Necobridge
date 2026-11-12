using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;

namespace NekoBridge.Core;

public sealed partial class BridgeNode
{
    private sealed record SourceFile(string Path, long Length, DateTime Modified, DateTime Created);
    private sealed record LocalOffer(Guid Recipient, FileOffer Manifest, DateTime Expires, Dictionary<int, SourceFile> Sources, Guid SourcePeer = default, Guid SourceOffer = default)
    {
        internal CancellationTokenSource Stop {get;}=new();
    }
    private readonly Dictionary<Guid, LocalOffer> offers = [];
    public FileOffer CreateOffer(Guid recipientId, IReadOnlyList<string> paths)
    {
        _ = PeerToken(recipientId);
        if (paths.Count is < 1 or > 10000) throw new ArgumentException("一次最多提供 10000 个项目。", nameof(paths));
        List<FileEntry> entries = []; Dictionary<int, SourceFile> sources = []; long total = 0;
        foreach (string path in paths)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            Add(full, Path.GetFileName(full));
        }
        var manifest = new FileOffer { Id = Guid.NewGuid(), Entries = entries, TotalBytes = total, ItemCount = paths.Count, DisplayName = paths.Count == 1 ? entries[0].RelativePath : $"{paths.Count} 个项目" };
        ValidateOffer(manifest);
        if (JsonSerializer.SerializeToUtf8Bytes(new WireMessage { Type = "offer", Offer = manifest }, Wire.Json).Length > Wire.MaxFrame) throw new IOException("文件清单超过协议大小限制。");
        RegisterOffer(new(recipientId, manifest, DateTime.UtcNow.AddHours(1), sources));
        return CloneOffer(manifest);
        void Add(string full, string relative)
        {
            if (entries.Count >= 10000) throw new IOException("文件清单最多包含 10000 个文件和目录。");
            if (relative.Length > 2000 || relative.Split('/').Length > 128 || relative.Replace('\\', '/').Split('/').Any(UnsafeSegment)) throw new IOException("文件路径过长或包含不支持的名称。");
            CheckNoLinks(full);
            var attributes = File.GetAttributes(full);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            int index = entries.Count;
            long length = 0;
            if (!directory)
            {
                var info = new FileInfo(full); length = info.Length; total = checked(total + length);
                sources[index] = new(full, length, info.LastWriteTimeUtc, info.CreationTimeUtc);
            }
            entries.Add(new FileEntry { Index = index, RelativePath = relative.Replace('\\', '/'), Length = length, IsDirectory = directory });
            if (directory) foreach (string child in Directory.EnumerateFileSystemEntries(full)) Add(child, relative + "/" + Path.GetFileName(child));
        }
    }
    public FileOffer CreateRelayOffer(Guid recipientId, Guid sourcePeerId, FileOffer sourceOffer)
    {
        _ = PeerToken(recipientId); _ = PeerToken(sourcePeerId);
        if (recipientId == sourcePeerId) throw new ArgumentException("不能将文件中继回来源设备。");
        ValidateOffer(sourceOffer);
        var manifest = CloneOffer(sourceOffer) with { Id = Guid.NewGuid() };
        RegisterOffer(new(recipientId, manifest, DateTime.UtcNow.AddHours(1), [], sourcePeerId, sourceOffer.Id));
        return CloneOffer(manifest);
    }
    private void RegisterOffer(LocalOffer offer)
    {
        lock (gate)
        {
            foreach (Guid expired in offers.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) offers.Remove(expired);
            if (offers.Count >= 64) throw new IOException("同时有效的拖放请求过多，请取消旧请求。");
            offers.Add(offer.Manifest.Id, offer);
        }
    }
    public void RetireOffer(Guid offerId){lock(gate)offers.Remove(offerId);}
    public void RevokeOffer(Guid offerId)
    {
        LocalOffer? offer;
        lock(gate){offers.Remove(offerId,out offer);}
        offer?.Stop.Cancel();
    }
    private LocalOffer FindOffer(Guid peerId, Guid offerId)
    {
        lock (gate)
        {
            if (!offers.TryGetValue(offerId, out var offer) || offer.Recipient != peerId || offer.Expires <= DateTime.UtcNow) throw new AuthenticationException("文件请求不存在、已到期或不属于该设备。");
            return offer;
        }
    }
    private static FileOffer CloneOffer(FileOffer offer) => offer with { Entries = offer.Entries.Select(e => e with { }).ToList() };
    private (string Host, int Port, string Fingerprint) PeerEndpoint(Guid id)
    {
        lock (gate)
        {
            var peer = settings.Peers.FirstOrDefault(p => p.Id == id) ?? throw new AuthenticationException("设备未配对。");
            return (peer.Host, peer.Port, peer.Fingerprint);
        }
    }
    private async Task<(TcpClient Client, SslStream Stream, CancellationTokenSource Lifetime)> OpenChannelAsync(Guid peerId, string mode, Guid offerId, int index, CancellationToken ct)
    {
        var endpoint = PeerEndpoint(peerId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, PeerToken(peerId));
        try
        {
            var channel = await DialAsync(endpoint.Host, endpoint.Port, null, mode, offerId, index, linked.Token).ConfigureAwait(false);
            if (channel.Device.Id != peerId || !string.Equals(channel.Fingerprint, endpoint.Fingerprint, StringComparison.OrdinalIgnoreCase)) { channel.Stream.Dispose(); channel.Client.Dispose(); throw new AuthenticationException("文件通道设备身份不匹配。"); }
            _ = linked.Token.Register(static state => ((TcpClient)state!).Dispose(), channel.Client);
            return (channel.Client, channel.Stream, linked);
        }
        catch { linked.Dispose(); throw; }
    }
    public async Task<FileOffer> GetOfferAsync(Guid peerId, Guid offerId, CancellationToken cancellationToken = default)
    {
        var channel = await OpenChannelAsync(peerId, "offer", offerId, 0, cancellationToken).ConfigureAwait(false);
        using (channel.Client) using (channel.Stream) using (channel.Lifetime)
        {
            channel.Lifetime.CancelAfter(TimeSpan.FromSeconds(15));
            var reply = await Wire.ReadAsync(channel.Stream, channel.Lifetime.Token);
            if (reply.Type != "offer" || reply.Offer == null || reply.Offer.Id != offerId) throw new IOException("远端未提供有效文件清单。");
            ValidateOffer(reply.Offer); return reply.Offer;
        }
    }
    public async Task<Stream> OpenRemoteFileAsync(Guid peerId, Guid offerId, int index, CancellationToken cancellationToken = default)
    {
        var channel = await OpenChannelAsync(peerId, "file", offerId, index, cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(channel.Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var reply = await Wire.ReadAsync(channel.Stream, timeout.Token).ConfigureAwait(false);
            if (reply.Type != "file" || reply.Length < 0) throw new IOException("远端无法打开该文件。");
            var remote = new RemoteReadStream(channel.Client, channel.Stream, reply.Length, channel.Lifetime);
            if (reply.Length == 0) _ = await remote.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
            return remote;
        }
        catch { channel.Stream.Dispose(); channel.Client.Dispose(); channel.Lifetime.Dispose(); throw; }
    }
    private async Task ServeFileRequestAsync(SslStream stream, WireMessage request, CancellationToken ct)
    {
        var offer = FindOffer(request.Device!.Id, request.OfferId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct,offer.Stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if (request.Mode == "offer") { await Wire.WriteAsync(stream, new WireMessage { Type = "offer", Offer = offer.Manifest }, timeout.Token).ConfigureAwait(false); return; }
        FileEntry entry = offer.Manifest.Entries.FirstOrDefault(e => e.Index == request.Index && !e.IsDirectory) ?? throw new IOException("文件索引无效。");
        Stream source;
        SourceFile? original = null;
        if (offer.SourcePeer != Guid.Empty) source = await OpenRemoteFileAsync(offer.SourcePeer, offer.SourceOffer, entry.Index, timeout.Token).ConfigureAwait(false);
        else
        {
            original = offer.Sources[entry.Index]; ValidateSource(original);
            var file = new FileStream(original.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try { ValidateSource(original); source = file; }
            catch { file.Dispose(); throw; }
        }
        await using (source)
        {
            if (source.Length != entry.Length) throw new IOException("来源文件大小已变化。");
            await Wire.WriteAsync(stream, new WireMessage { Type = "file", Length = entry.Length }, timeout.Token).ConfigureAwait(false);
            byte[] buffer = new byte[65536]; long sent = 0; DateTime last = DateTime.MinValue;
            var progressOffer = offer.Manifest with { DisplayName = entry.RelativePath };
            try
            {
                Progress(request.Device.Id, progressOffer, 0, entry.Length, "正在发送", ref last, true);
                while (sent < entry.Length)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.Length - sent)), timeout.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("来源文件提前结束。");
                    await stream.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false); sent += count;
                    Progress(request.Device.Id, progressOffer, sent, entry.Length, "正在发送", ref last);
                }
                // Reading once more verifies relay completion, and detects source growth.
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                if (await source.ReadAsync(buffer.AsMemory(0, 1), timeout.Token) != 0) throw new IOException("来源文件大小已变化。");
                if (original != null) ValidateSource(original);
                timeout.Token.ThrowIfCancellationRequested();
                await Wire.WriteAsync(stream, new WireMessage { Type = "complete", Length = sent }, timeout.Token).ConfigureAwait(false);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var acknowledgement = await Wire.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (acknowledgement.Type != "received" || acknowledgement.Length != sent) throw new IOException("接收端未确认完整读取文件。");
                Progress(request.Device.Id, progressOffer, sent, entry.Length, "已发送", ref last, true);
            }
            catch { Progress(request.Device.Id, progressOffer, sent, entry.Length, "发送失败", ref last, true); throw; }
        }
    }
    public async Task DownloadOfferAsync(Guid peerId, Guid offerId, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        FileOffer offer = await GetOfferAsync(peerId, offerId, cancellationToken).ConfigureAwait(false);
        string destination = Path.GetFullPath(destinationDirectory); CheckNoLinks(destination, true); Directory.CreateDirectory(destination); CheckNoLinks(destination);
        var roots = offer.Entries.Where(e => !e.RelativePath.Contains('/')).ToArray();
        void EnsureNoCollisions()
        {
            foreach (var root in roots)
                if (Path.Exists(Path.Combine(destination, root.RelativePath))) throw new IOException($"目标已有同名项目：{root.RelativePath}。未覆盖现有文件。");
        }
        EnsureNoCollisions();
        string staging = Path.Combine(destination, ".nekobridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        long bytes = 0; DateTime last = DateTime.MinValue;
        var moved = new List<(string Source, string Destination, bool Directory)>();
        try
        {
            Progress(peerId, offer, bytes, offer.TotalBytes, "正在接收", ref last, true);
            foreach (var entry in offer.Entries.OrderBy(e => e.RelativePath.Count(c => c == '/')).ThenBy(e => e.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(staging, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                CheckNoLinks(staging);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CheckNoLinks(Path.GetDirectoryName(target)!);
                await using Stream remote = await OpenRemoteFileAsync(peerId, offer.Id, entry.Index, cancellationToken).ConfigureAwait(false);
                if (remote.Length != entry.Length) throw new IOException("远端文件长度与清单不一致。");
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[65536]; long received = 0;
                while (true)
                {
                    int count = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false); received += count; bytes += count;
                    Progress(peerId, offer, bytes, offer.TotalBytes, "正在接收", ref last);
                }
                if (received != entry.Length) throw new IOException("接收字节数不匹配。");
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); CheckNoLinks(destination);
            EnsureNoCollisions();
            // Commit is deliberately non-cancellable; roll back our own renames on a collision/error.
            foreach (var root in roots)
            {
                string from = Path.Combine(staging, root.RelativePath), to = Path.Combine(destination, root.RelativePath);
                if (root.IsDirectory) Directory.Move(from, to); else File.Move(from, to, false);
                moved.Add((from, to, root.IsDirectory));
            }
            Progress(peerId, offer, bytes, offer.TotalBytes, "已完成", ref last, true);
        }
        catch (Exception failure)
        {
            Exception? rollbackFailure = null;
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                try { if (item.Directory) Directory.Move(item.Destination, item.Source); else File.Move(item.Destination, item.Source, false); }
                catch (Exception ex) { rollbackFailure = ex; }
            }
            Progress(peerId, offer, bytes, offer.TotalBytes, failure is OperationCanceledException ? "已取消" : "接收失败", ref last, true);
            if (rollbackFailure != null) throw new AggregateException("提交失败，部分已接收项目无法移回暂存目录；请检查接收目录。", failure, rollbackFailure);
            throw;
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (Exception ex) { Report("无法清理接收暂存目录 " + staging + "：" + ex.Message); }
        }
    }
    private void Progress(Guid peer, FileOffer offer, long bytes, long total, string state, ref DateTime last, bool force = false)
    {
        if (!force && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(150)) return;
        last = DateTime.UtcNow;
        try { TransferProgress?.Invoke(peer, new(offer.Id, offer.DisplayName, bytes, total, state)); } catch (Exception ex) { Report("传输进度处理失败：" + ex.Message); }
    }
    private static void ValidateSource(SourceFile source)
    {
        CheckNoLinks(source.Path); var info = new FileInfo(source.Path);
        if (!info.Exists || info.Length != source.Length || info.LastWriteTimeUtc != source.Modified || info.CreationTimeUtc != source.Created) throw new IOException("来源文件已更改，请重新拖放。");
    }
    private static void CheckNoLinks(string path, bool allowMissing = false)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (!allowMissing || Path.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持符号链接或重解析点：" + path);
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }
    private static void ValidateOffer(FileOffer offer)
    {
        if (offer.Id == Guid.Empty || offer.Entries == null || offer.Entries.Count is < 1 or > 10000 || offer.DisplayName == null || offer.DisplayName.Length > 200 || offer.DisplayName.Any(char.IsControl)) throw new InvalidDataException("文件清单无效。");
        Dictionary<string, FileEntry> paths = new(StringComparer.OrdinalIgnoreCase); HashSet<int> indexes = []; long total = 0;
        foreach (var entry in offer.Entries)
        {
            if (entry == null || entry.Index < 0 || !indexes.Add(entry.Index) || entry.Length < 0 || (entry.IsDirectory && entry.Length != 0)) throw new InvalidDataException("文件条目无效。");
            string path = entry.RelativePath;
            if (string.IsNullOrWhiteSpace(path) || path.Length > 2000 || path.Contains('\\') || path.Split('/').Any(UnsafeSegment) || !paths.TryAdd(path, entry)) throw new InvalidDataException("文件相对路径不安全或重复。");
            total = checked(total + entry.Length);
        }
        foreach (string path in paths.Keys)
        {
            int slash = path.LastIndexOf('/');
            if (slash >= 0 && (!paths.TryGetValue(path[..slash], out var parent) || !parent.IsDirectory)) throw new InvalidDataException("文件清单缺少父目录。");
        }
        int roots = paths.Keys.Count(p => !p.Contains('/'));
        if (total != offer.TotalBytes || offer.ItemCount != roots) throw new InvalidDataException("文件清单统计不匹配。");
    }
    private static bool UnsafeSegment(string segment)
    {
        if (segment.Length is < 1 or > 240 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))) return true;
        string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }
}

internal sealed class RemoteReadStream(TcpClient client, SslStream stream, long length, CancellationTokenSource lifetime) : Stream
{
    private long position;
    private bool complete;
    private bool disposed;
    private readonly SemaphoreSlim reading = new(1);
    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.Length == 0) return 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await reading.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (complete) return 0;
            int count = 0;
            if (position < length)
            {
                count = await stream.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], timeout.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("文件通道意外断开。");
                position += count;
            }
            if (position == length)
            {
                var trailer = await Wire.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (trailer.Type != "complete" || trailer.Length != length) throw new IOException("远端未确认文件完整发送。");
                await Wire.WriteAsync(stream, new WireMessage { Type = "received", Length = position }, timeout.Token).ConfigureAwait(false);
                complete = true;
            }
            return count;
        }
        catch { Dispose(); throw; }
        finally { reading.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing) { disposed = true; lifetime.Cancel(); stream.Dispose(); client.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
