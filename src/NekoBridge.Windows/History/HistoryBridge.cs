using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NekoBridge.Core;

namespace NekoBridge.Windows.History;

public sealed class HistoryBridge : IDisposable
{
    private readonly ClipboardSync clipboard;
    private readonly HistoryStore store;
    private readonly BridgeNode node;
    private readonly Action<string> log;
    private readonly Func<string, bool> shouldCapture;
    private readonly SemaphoreSlim saves = new(1);
    private readonly CancellationTokenSource lifetime = new();
    public HistoryBridge(ClipboardSync clipboard, HistoryStore store, BridgeNode node, Action<string> log,
        Func<string, bool>? shouldCapture = null)
    {
        this.clipboard = clipboard; this.store = store; this.node = node; this.log = log;
        this.shouldCapture = shouldCapture ?? (_ => true);
        clipboard.ContentSucceeded += Capture;
    }
    private readonly System.Collections.Generic.List<Task> captures = [];
    private readonly System.Collections.Generic.List<Task> fileSaves = [];
    private bool stopping;
    private void Capture(ClipboardSync.SuccessfulContent content)
    {
        if (!shouldCapture(content.Rich?.Kind ?? "text")) return;
        lock (captures)
        {
            if (stopping) return;
            captures.RemoveAll(t => t.IsCompleted); captures.Add(CaptureAsync(content));
        }
    }
    public async Task StopAsync()
    {
        clipboard.ContentSucceeded -= Capture;
        Task[] pending;
        lock (captures)
        {
            stopping = true;
            pending = captures.Concat(fileSaves).ToArray();
        }
        lifetime.Cancel();
        // Callers own operation errors. Shutdown must still wait for every save's
        // cancellation and directory cleanup before the store can be disposed.
        try { await Task.WhenAll(pending); }
        catch { }
    }
    private async Task CaptureAsync(ClipboardSync.SuccessfulContent content)
    {
        try
        {
            var rich = content.Rich;
            Guid source = rich is { Kind: "files", Paths: null, Peer: var peer } && peer != Guid.Empty
                ? peer : content.From == Guid.Empty ? node.LocalId : content.From;
            string name = source == node.LocalId ? node.LocalDevice.Name : node.Peers.FirstOrDefault(p => p.Id == source)?.DisplayName ?? "远端设备";
            await store.AddAsync(new HistoryCapture(content.Id, rich?.Kind ?? "text", source, name,
                content.Text, rich?.Png, rich?.Paths, rich?.Offer?.Id,
                rich?.Offer?.DisplayName), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { log("剪贴板历史记录保存失败。"); }
    }
    public async Task RestoreAsync(HistoryEntry entry, CancellationToken token = default)
        => await clipboard.RestoreHistoryAsync(entry, entry.Kind == "image" ? await store.ReadImageAsync(entry.Id, token) : null, token);

    // Explicit user action only: never eagerly download clipboard file offers.
    public Task<string[]> SaveFileCopyAsync(HistoryEntry entry, CancellationToken token = default)
    {
        lock (captures)
        {
            if (stopping) return Task.FromCanceled<string[]>(new CancellationToken(true));
            fileSaves.RemoveAll(t => t.IsCompleted);
            var task = SaveFileCopyCoreAsync(entry, token);
            fileSaves.Add(task);
            return task;
        }
    }
    private async Task<string[]> SaveFileCopyCoreAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        CancellationToken token = linked.Token;
        if (entry.Kind != "files") throw new InvalidOperationException("只有文件记录可以保存副本。");
        await saves.WaitAsync(token);
        string? directory = null;
        bool ownsDirectory = false;
        try
        {
            if (entry.SavedPaths is { Length: > 0 } existing && existing.All(Path.Exists)) return existing;
            directory = store.GetSaveDirectory(entry.Id);
            if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("副本目录非空，请先清理未完成的副本。");
            ownsDirectory = true;
            long bytes;
            if (entry.SourceMachine == node.LocalId && entry.Paths is { Length: > 0 } local)
            {
                bytes = 0; int count = 0;
                async Task Copy(string source, string target, int depth)
                {
                    token.ThrowIfCancellationRequested();
                    if (depth > 128 || ++count > 10000) throw new IOException("文件数量或目录深度超过限制。");
                    for (string? check = Path.GetFullPath(source); check != null; check = Path.GetDirectoryName(check))
                        if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接或重解析点。");
                    if (Directory.Exists(source))
                    {
                        Directory.CreateDirectory(target);
                        foreach (string child in Directory.EnumerateFileSystemEntries(source)) await Copy(child, Path.Combine(target, Path.GetFileName(child)), depth + 1);
                    }
                    else
                    {
                        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        long expected = input.Length;
                        bytes = checked(bytes + expected);
                        if (bytes > 512L * 1024 * 1024) throw new IOException("离线文件副本超过512MiB限制。");
                        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        byte[] buffer = new byte[65536]; long copied = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, token)) != 0)
                        {
                            copied += read;
                            if (copied > expected) throw new IOException("来源文件已更改。");
                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                        }
                        if (copied != expected) throw new IOException("来源文件已更改。");
                    }
                }
                foreach (string source in local) await Task.Run(() => Copy(source, Path.Combine(directory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source))), 0), token);
            }
            else
            {
                if (!entry.OfferId.HasValue || !node.Peers.Any(p => p.Id == entry.SourceMachine && p.IsConnected)) throw new IOException("来源设备离线，无法保存文件副本。");
                FileOffer offer;
                try { offer = await node.GetOfferAsync(entry.SourceMachine, entry.OfferId.Value, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new IOException("历史文件分享已失效，请在来源设备重新复制。", ex); }
                bytes = offer.TotalBytes;
                if (bytes > 512L * 1024 * 1024) throw new IOException("离线文件副本超过512MiB限制。");
                await node.DownloadOfferAsync(entry.SourceMachine, offer.Id, directory, token);
            }
            string[] paths = Directory.GetFileSystemEntries(directory);
            await store.AttachSavedFilesAsync(entry.Id, paths, bytes, token);
            directory = null;
            return paths;
        }
        catch
        {
            if (directory != null && ownsDirectory) { try { Directory.Delete(directory, true); } catch { log("未完成的历史副本无法清理。"); } }
            throw;
        }
        finally { saves.Release(); }
    }
    public void Dispose() { clipboard.ContentSucceeded -= Capture; lifetime.Cancel(); }
}
