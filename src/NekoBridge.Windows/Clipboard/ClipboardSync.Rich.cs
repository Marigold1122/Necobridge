using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NekoBridge.Core;

namespace NekoBridge.Windows;

public sealed partial class ClipboardSync
{
    private sealed record RichReference(string Kind, Guid Offer);
    private readonly Dictionary<Guid, string> _imageOfferDirectories = [];
    private readonly HashSet<string> _preparingImages = [];

    public void Receive(Guid from, ControlMessage message)
    {
        if (message.Kind != "clipboard") return;
        if (message.Payload == null) { if (message.Text != null) Receive(from, message.Text, message.Id); return; }
        if (message.Payload.Length > 1024 || _node == null) return;
        RichReference? reference;
        try { reference = JsonSerializer.Deserialize<RichReference>(message.Payload); }
        catch (JsonException) { return; }
        if (reference == null || reference.Kind is not ("files" or "image") || reference.Offer == Guid.Empty) return;
        OnDispatcher(() =>
        {
            if (!CanShare() || !_peers().Contains(from)) return;
            Guid id = message.Id == Guid.Empty ? Guid.NewGuid() : message.Id;
            if (!_seen.Add(id)) return;
            Remember(id);
            _retry.Stop();_pending?.Completion?.TrySetCanceled();_pending = null;
            long generation = ++_generation;
            uint sequence = _clipboard.Sequence;
            _ = LoadRich(from, reference, id, generation, sequence, _session.Token);
        });
    }

    private async Task PrepareLocalFiles(RichClipboard content, Pending pending, long generation, CancellationToken token)
    {
        try
        {
            string fingerprint = await Task.Run(() =>
            {
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                int count = 0;
                void Add(string path, int depth = 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (depth > 128) throw new IOException("Clipboard directory tree is too deep.");
                    if (++count > 10000) throw new IOException("Too many clipboard files.");
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked clipboard source is not supported.");
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    string metadata = JsonSerializer.Serialize(new { Path = Path.GetFullPath(path), Directory = directory,
                        Length = directory ? 0 : new FileInfo(path).Length, Modified = File.GetLastWriteTimeUtc(path).Ticks,
                        Created = File.GetCreationTimeUtc(path).Ticks });
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(metadata));
                    if (directory) foreach (string child in Directory.EnumerateFileSystemEntries(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) Add(child, depth + 1);
                }
                foreach (string path in content.Paths!) Add(path);
                return "local-files:" + Convert.ToHexString(hash.GetHashAndReset());
            }, token);
            if (!CanObserve() || token.IsCancellationRequested || generation != _generation) return;
            if (_clipboard.Sequence != pending.Sequence) { _observedSequence = null; HandleClipboardUpdate(); return; }
            Begin(pending with { Rich = content with { Fingerprint = fingerprint } });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested && !_disposed) _log("剪贴板文件检查失败，请重新复制。"); }
    }

    private async Task LoadRich(Guid from, RichReference reference, Guid id, long generation, uint sequence, CancellationToken token)
    {
        try
        {
            RichClipboard content = await Task.Run(async () =>
            {
                // Fetch the manifest through the authenticated offer channel, never
                // deserialize/trust a sender-supplied manifest or local source path.
                FileOffer offer = await _node!.GetOfferAsync(from, reference.Offer, token).ConfigureAwait(false);
                if (reference.Kind == "files")
                    return new RichClipboard("files", RichClipboard.Digest($"file-offer:{from}:{offer.Id}"), Peer: from, Offer: offer);
                if (offer.Entries.Count != 1 || offer.Entries[0].IsDirectory || offer.TotalBytes is < 33 or > RichClipboard.MaxBytes)
                    throw new InvalidDataException();
                FileEntry entry = offer.Entries[0];
                await using Stream remote = await _node.OpenRemoteFileAsync(from, offer.Id, entry.Index, token).ConfigureAwait(false);
                if (remote.Length != entry.Length) throw new InvalidDataException();
                byte[] png = new byte[checked((int)entry.Length)];
                await remote.ReadExactlyAsync(png, token).ConfigureAwait(false);
                if (await remote.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException();
                return RichClipboard.Image(png) with { Peer = from, Offer = offer };
            }, token);
            if (!CanShare() || token.IsCancellationRequested || generation != _generation || !_peers().Contains(from)) return;
            if (_clipboard.Sequence != sequence) { _observedSequence = null; HandleClipboardUpdate(); return; }
            Begin(new Pending(null, from, id, sequence, content));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested && !_disposed) _log("剪贴板图片或文件获取失败，本次同步已停止。"); }
    }

    private async Task PublishRich(RichClipboard content, Guid from, Guid id, long generation, CancellationToken token)
    {
        if (_node == null) return;
        string[]? paths = content.Paths;
        string? preparing = null;
        try
        {
            if (content.Kind == "image" && content.Offer == null)
            {
                // Keep source PNG available for the offer's full lifetime. Only
                // app-owned, GUID-named directories are ever removed.
                PruneImages();
                long retainedBytes = _imageDirectories.Sum(d => Directory.Exists(d) ? Directory.EnumerateFiles(d).Sum(p => new FileInfo(p).Length) : 0);
                if (_imageDirectories.Count >= 32 || retainedBytes + content.Png!.Length > 256L * 1024 * 1024)
                    throw new IOException("Clipboard image cache is full.");
                string directory = Path.Combine(Path.GetTempPath(), "NekoBridge", "clipboard-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                _imageDirectories.Add(directory);
                _preparingImages.Add(directory); preparing = directory;
                string path = Path.Combine(directory, "image.png");
                await File.WriteAllBytesAsync(path, content.Png!, token);
                paths = [path];
            }
            foreach (Guid peer in _peers())
            {
                if (peer == from) continue;
                if (token.IsCancellationRequested || !CanShare() || generation != _generation) return;
                FileOffer offer = await Task.Run(() => content.Offer != null
                    ? _node.CreateRelayOffer(peer, content.Peer, content.Offer)
                    : _node.CreateOffer(peer, paths!), token);
                if (token.IsCancellationRequested || !CanShare() || generation != _generation || !_peers().Contains(peer))
                { _node.RevokeOffer(offer.Id); return; }
                if (content.Kind == "image" && content.Offer == null) _imageOfferDirectories[offer.Id] = Path.GetDirectoryName(paths![0])!;
                TrackOffer(peer, offer.Id);
                QueueSend(peer, new ControlMessage { Kind = "clipboard", Id = id,
                    Payload = JsonSerializer.Serialize(new RichReference(content.Kind, offer.Id)) });
            }
            PruneImages();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested && !_disposed) _log("剪贴板图片或文件发送失败，请重新复制或检查连接。"); }
        finally { if (preparing != null) _preparingImages.Remove(preparing); PruneImages(); }
    }

    private void TrackOffer(Guid peer, Guid offer)
    {
        if (!_richOffers.TryGetValue(peer, out Queue<Guid>? recent)) _richOffers.Add(peer, recent = new());
        recent.Enqueue(offer);
        // Keep the latest two generations per recipient. Existing source reads
        // must survive revocation (Core coordinates the active-reader lifetime).
        while (recent.Count > 2) Retire(recent.Dequeue());
        while (_richOffers.Sum(p => p.Value.Count) > 32)
        {
            Queue<Guid> oldest = _richOffers.First(p => p.Value.Count > 0).Value;
            Retire(oldest.Dequeue());
        }
    }
    private void Retire(Guid offer)
    {
        _node!.RetireOffer(offer);
        _imageOfferDirectories.Remove(offer);
    }
    private void PruneImages()
    {
        // Images are tiny compared with source files but still bounded. They are
        // retained one hour, matching Core offer expiry, not eagerly deleted.
        foreach (string directory in _imageDirectories.ToArray())
        {
            try
            {
                if (_preparingImages.Contains(directory)) continue;
                if (_imageOfferDirectories.ContainsValue(directory) && Directory.GetCreationTimeUtc(directory) > DateTime.UtcNow.AddHours(-1)) continue;
                Directory.Delete(directory, true); _imageDirectories.Remove(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task RestoreHistoryAsync(NekoBridge.Windows.History.HistoryEntry entry, byte[]? png, CancellationToken token = default)
    {
        RichClipboard? rich = null;
        string? text = entry.Text;
        if (entry.Kind == "image") rich = RichClipboard.Image(png ?? throw new IOException("历史图片不存在。"));
        else if (entry.Kind == "files")
        {
            string[]? paths = entry.SavedPaths ?? (entry.SourceMachine == _node?.LocalId ? entry.Paths : null);
            if (paths != null)
            {
                foreach (string path in paths) if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("历史文件不存在或不再安全，请重新复制。");
                rich = new("files", RichClipboard.Digest("files:" + string.Join(" ", paths)), Paths: paths);
            }
            else
            {
                if (_node == null || !entry.OfferId.HasValue || !_peers().Contains(entry.SourceMachine)) throw new IOException("来源设备离线，且此文件尚未保存本地副本。");
                FileOffer offer;
                try { offer = await _node.GetOfferAsync(entry.SourceMachine, entry.OfferId.Value, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new IOException("历史文件分享已失效，请在来源设备重新复制。", ex); }
                rich = new("files", RichClipboard.Digest($"file-offer:{entry.SourceMachine}:{offer.Id}"), Peer: entry.SourceMachine, Offer: offer);
            }
        }
        else if (entry.Kind != "text" || text == null || text.Length > MaxTextLength || text.Contains('\0')) throw new IOException("历史内容无效。");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClipboardSync));
            Begin(new Pending(entry.Kind == "text" ? text : null, Guid.Empty, Guid.NewGuid(), _clipboard.Sequence, rich, completion, Capture: false));
        });
        using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        await completion.Task;
    }
}
