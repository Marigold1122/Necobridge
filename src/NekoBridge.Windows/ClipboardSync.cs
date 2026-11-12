// Copyright (c) Microsoft Corporation. Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using NekoBridge.Core;

namespace NekoBridge.Windows;

public sealed partial class ClipboardSync : IDisposable
{
    private const int MaxTextLength = 262144;
    private BridgeNode? _node;
    private long _generation;
    private RichClipboard? _richCurrent;
    private readonly Dictionary<Guid, Queue<Guid>> _richOffers = [];
    private readonly List<string> _imageDirectories = [];
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _historyEnabled;
    internal sealed record SuccessfulContent(Guid Id, Guid From, string? Text, RichClipboard? Rich);
    internal event Action<SuccessfulContent>? ContentSucceeded;
    private string? _historyHash;
    private void Observe(Pending pending, string hash, string? text, RichClipboard? rich)
    {
        if (!_historyEnabled() || _historyHash == hash) return;
        _historyHash = hash;
        try { ContentSucceeded?.Invoke(new(pending.Id, pending.From, text, rich)); }
        catch { _log("剪贴板历史记录保存失败。"); }
    }
    private readonly Action<string> _log;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Guid[]> _peers;
    private readonly Func<Guid, ControlMessage, CancellationToken, Task> _send;
    private readonly IClipboard _clipboard;
    private readonly DispatcherTimer _retry;
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = [];
    private readonly Dictionary<Guid, Outbox> _outboxes = [];
    private CancellationTokenSource _session = new();
    private bool _disposed;
    private bool _writing;
    private uint? _ownSequence;
    private uint? _observedSequence;
    private string? _lastHash;
    private Pending? _pending;
    private int _attempts;

    public ClipboardSync(BridgeNode node, Func<bool> enabled, Action<string> log, Dispatcher dispatcher, Func<bool>? historyEnabled = null)
        : this(enabled, log, dispatcher,
            () => node.Peers.Where(p => p.IsConnected).Select(p => p.Id).ToArray(),
            node.SendAsync, new NativeClipboard(), historyEnabled: historyEnabled) { _node = node; }

    internal ClipboardSync(Func<bool> enabled, Action<string> log, Dispatcher dispatcher,
        Func<Guid[]> peers, Func<Guid, ControlMessage, CancellationToken, Task> send, IClipboard clipboard, BridgeNode? node = null, Func<bool>? historyEnabled = null)
    {
        _node = node;
        _historyEnabled = historyEnabled ?? (() => false);
        _enabled = enabled; _log = log; _dispatcher = dispatcher;
        _peers = peers; _send = send; _clipboard = clipboard;
        _retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _retry.Tick += Retry;
    }

    // Call after a committed settings change, including when disabling sharing.
    public void EnabledChanged() => OnDispatcher(() =>
    {
        _generation++; _richCurrent = null;
        _retry.Stop(); _pending?.Completion?.TrySetCanceled(); _pending = null; _attempts = 0;
        _session.Cancel(); _session.Dispose(); _session = new();
        _outboxes.Clear(); _lastHash = null;
        _observedSequence = _clipboard.Sequence;
        // Keep seen IDs: a delayed in-flight echo must not become a new change.
    });

    public void HandleClipboardUpdate() => OnDispatcher(() =>
    {
        if (!CanObserve() || _writing) return;
        uint sequence = _clipboard.Sequence;
        if (_ownSequence == sequence || _observedSequence == sequence) return;
        _observedSequence = sequence;
        Begin(new Pending(null, Guid.Empty, Guid.NewGuid(), sequence));
    });

    public void Receive(Guid from, string text) => Receive(from, text, Guid.Empty);

    // Relays retain message IDs; content deduplication happens after clipboard access.
    public void Receive(Guid from, string text, Guid messageId)
    {
        if (text == null || text.Length > MaxTextLength || text.IndexOf('\0') >= 0) return;
        OnDispatcher(() =>
        {
            if (!CanShare() || !_peers().Contains(from)) return;
            if (messageId == Guid.Empty) messageId = Guid.NewGuid();
            if (!_seen.Add(messageId)) return;
            Remember(messageId);
            Begin(new Pending(text, from, messageId, _clipboard.Sequence));
        });
    }

    private void Begin(Pending pending)
    {
        _generation++;
        _retry.Stop(); _pending?.Completion?.TrySetCanceled(); _pending = pending; _attempts = 0;
        // Never open/read/write the clipboard from inside the window hook.
        _retry.Interval = TimeSpan.FromMilliseconds(1); _retry.Start();
    }

    private void Retry(object? sender, EventArgs e)
    {
        _retry.Stop();
        if (_pending is not { } pending || (!CanObserve() && pending.Completion == null)) return;
        if (pending.From != Guid.Empty && !CanShare()) { pending.Completion?.TrySetCanceled(); _pending = null; return; }
        if (pending.Completion?.Task.IsCompleted == true) { _pending = null; return; }
        // A newer local copy wins over an older remote write waiting on a lock,
        // even if WM_CLIPBOARDUPDATE has not yet been dispatched.
        if (_clipboard.Sequence != pending.Sequence)
        {
            pending.Completion?.TrySetCanceled(); _pending = null; _observedSequence = null;
            HandleClipboardUpdate(); return;
        }
        try
        {
            if (pending.From != Guid.Empty && (pending.Text != null || pending.Rich != null) && !_peers().Contains(pending.From)) { pending.Completion?.TrySetCanceled(); _pending = null; return; }
            RichClipboard? rich = _clipboard.ReadRich();
            if (_clipboard.Sequence != pending.Sequence) { pending.Completion?.TrySetCanceled(); _pending = null; _observedSequence = null; HandleClipboardUpdate(); return; }
            // OLE virtual files have no CF_HDROP. Our retained identity is valid only
            // while the sequence is still the one written by us.
            if (rich == null && _ownSequence == pending.Sequence) rich = _richCurrent;
            if (rich is { Kind: "files", Paths: null, Offer: null } && _richCurrent?.Offer is { } retained &&
                rich.Fingerprint == RichClipboard.FilesFingerprint(retained.Entries)) rich = _richCurrent;
            if (pending.Rich == null && pending.Text == null && rich?.Paths != null)
            {
                _pending = null;
                _ = PrepareLocalFiles(rich, pending, _generation, _session.Token);
                return;
            }
            if (pending.Rich != null || (pending.Text == null && rich != null))
            {
                RichClipboard content = pending.Rich ?? rich!;
                if (content.Kind == "files" && content.Paths == null && content.Offer == null)
                {
                    if (_richCurrent?.Fingerprint == content.Fingerprint) content = _richCurrent;
                    else { pending.Completion?.TrySetException(new InvalidOperationException("剪贴板文件引用已失效。")); _pending = null; _lastHash = null; return; }
                }
                bool richChanged = pending.Rich != null && (pending.From != Guid.Empty || pending.Completion != null) && rich?.Fingerprint != content.Fingerprint;
                if (richChanged)
                {
                    _writing = true;
                    try { _ownSequence = _clipboard.WriteRich(content, _node!, _log); _observedSequence = _ownSequence; }
                    finally { _writing = false; }
                }
                _richCurrent = content; _pending = null;
                if (pending.Capture && (pending.From == Guid.Empty || richChanged)) Observe(pending, content.Fingerprint, null, content);
                pending.Completion?.TrySetResult();
                if (!richChanged && _lastHash == content.Fingerprint) return;
                _lastHash = content.Fingerprint;
                if (_seen.Add(pending.Id)) Remember(pending.Id);
                if (CanShare()) _ = PublishRich(content, pending.From, pending.Id, _generation, _session.Token);
                return;
            }
            _richCurrent = null;
            string? current = rich == null ? _clipboard.Read() : null;
            if (_clipboard.Sequence != pending.Sequence)
            {
                pending.Completion?.TrySetCanceled(); _pending = null; _observedSequence = null;
                HandleClipboardUpdate(); return;
            }
            bool changed = pending.Text != null && !string.Equals(current, pending.Text, StringComparison.Ordinal);
            if (changed)
            {
                _writing = true;
                try
                {
                    _ownSequence = _clipboard.Write(pending.Text!);
                    _observedSequence = _ownSequence;
                }
                finally { _writing = false; }
            }
            string? text = pending.Text ?? current;
            _pending = null;
            if (text == null) { pending.Completion?.TrySetException(new InvalidOperationException("剪贴板没有可恢复的文本。")); _lastHash = null; return; }
            if (text.Length > MaxTextLength || text.IndexOf('\0') >= 0)
            { pending.Completion?.TrySetException(new InvalidOperationException("剪贴板文本超过限制或包含无效字符。")); _lastHash = null; _log("剪贴板文本超过共享限制，未发送。"); return; }
            string hash = Hash(text);
            if (pending.Capture && (pending.From == Guid.Empty || changed)) Observe(pending, hash, text, null);
            pending.Completion?.TrySetResult();
            // A real B -> A write must propagate even when the last shared value was A.
            if (!changed && _lastHash == hash) return;
            _lastHash = hash;
            if (_seen.Add(pending.Id)) Remember(pending.Id);
            foreach (Guid peer in CanShare() ? _peers() : Array.Empty<Guid>())
                if (peer != pending.From) QueueSend(peer, new ControlMessage { Kind = "clipboard", Id = pending.Id, Text = text });
        }
        catch (ExternalException)
        {
            // Initial attempt plus ten retries.
            if (++_attempts <= 10)
            { _retry.Interval = TimeSpan.FromMilliseconds(100); _retry.Start(); }
            else { pending.Completion?.TrySetException(new InvalidOperationException("剪贴板持续被占用。")); _pending = null; _log("剪贴板持续被占用，本次同步已停止；下次复制会重新尝试。"); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or NotSupportedException or OverflowException)
        { pending.Completion?.TrySetException(ex); _pending = null; _log("剪贴板同步失败，本次内容未发送。"); }
    }

    private void QueueSend(Guid peer, ControlMessage message)
    {
        if (_outboxes.TryGetValue(peer, out var existing)) { existing.Next = message; return; }
        var box = new Outbox { Next = message };
        _outboxes.Add(peer, box);
        _ = Drain(peer, box, _session.Token);
    }

    private async Task Drain(Guid peer, Outbox box, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && CanShare() && box.Next is { } message)
            {
                box.Next = null;
                if (!_peers().Contains(peer)) break;
                try { await _send(peer, message, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception) { _log("剪贴板发送失败，请检查设备连接。"); break; }
            }
        }
        finally
        {
            if (_outboxes.TryGetValue(peer, out var current) && ReferenceEquals(current, box))
                _outboxes.Remove(peer);
        }
    }

    private void Remember(Guid id)
    {
        _seenOrder.Enqueue(id);
        if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(text)));
    private bool CanObserve() => !_disposed && (_enabled() || _historyEnabled());
    private bool CanShare() => !_disposed && _enabled();
    private void OnDispatcher(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    public void Dispose()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(Dispose); return; }
        if (_disposed) return;
        _disposed = true; _retry.Stop(); _retry.Tick -= Retry;
        _pending?.Completion?.TrySetCanceled(); _pending = null; _session.Cancel(); _session.Dispose();
        _outboxes.Clear(); _seen.Clear(); _seenOrder.Clear(); _lastHash = null;
        _clipboard.Dispose();
        foreach (var offers in _richOffers.Values) foreach (Guid offer in offers) _node?.RevokeOffer(offer);
        _richOffers.Clear();
        foreach (string directory in _imageDirectories)
        {
            try { System.IO.Directory.Delete(directory, true); }
            catch (System.IO.IOException) { _log("剪贴板图片缓存仍在读取，将保留暂存文件。"); }
            catch (UnauthorizedAccessException) { _log("剪贴板图片缓存清理失败。"); }
        }
        _imageDirectories.Clear();
    }

    private sealed record Pending(string? Text, Guid From, Guid Id, uint Sequence, RichClipboard? Rich = null, TaskCompletionSource? Completion = null, bool Capture = true);
    private sealed class Outbox { public ControlMessage? Next; }

    internal interface IClipboard : IDisposable
    {
        uint Sequence { get; }
        string? Read();
        uint Write(string text);
        RichClipboard? ReadRich() => null;
        uint WriteRich(RichClipboard content, BridgeNode node, Action<string> log) => throw new InvalidOperationException();
    }

    // One nonblocking OpenClipboard attempt per timer tick. A private message
    // window owns writes; OpenClipboard(NULL) + EmptyClipboard cannot safely
    // be used with SetClipboardData. The OS owns memory after successful Set.
    private sealed class NativeClipboard : IClipboard
    {
        private const uint UnicodeText = 13;
        private readonly HwndSource _owner = new(new HwndSourceParameters("NekoBridge.Clipboard")
        { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        public uint Sequence => GetClipboardSequenceNumber();
        public RichClipboard? ReadRich() => RichClipboard.Read();
        public uint WriteRich(RichClipboard content, BridgeNode node, Action<string> log)
        { return content.Write(node, log); }
        public string? Read()
        {
            Open();
            try
            {
                if (!IsClipboardFormatAvailable(UnicodeText)) return null;
                nint handle = GetClipboardData(UnicodeText);
                if (handle == 0) throw new ExternalException();
                nuint bytes = GlobalSize(handle);
                // GlobalSize may include allocator padding. Scan only the
                // allowed prefix rather than rejecting a valid padded buffer.
                nint data = GlobalLock(handle);
                if (data == 0) throw new ExternalException();
                try
                {
                    int length = (int)Math.Min((ulong)(bytes / 2), (ulong)MaxTextLength + 1);
                    for (int i = 0; i < length; i++)
                        if (Marshal.ReadInt16(data, i * 2) == 0) return Marshal.PtrToStringUni(data, i);
                    throw new InvalidOperationException();
                }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        public uint Write(string text)
        {
            nint memory = GlobalAlloc(0x0002, (nuint)((text.Length + 1) * 2));
            if (memory == 0) throw new ExternalException();
            try
            {
                nint data = GlobalLock(memory);
                if (data == 0) throw new ExternalException();
                try
                {
                    char[] chars = (text + '\0').ToCharArray();
                    Marshal.Copy(chars, 0, data, chars.Length);
                }
                finally { GlobalUnlock(memory); }
                Open();
                try
                {
                    if (!EmptyClipboard() || SetClipboardData(UnicodeText, memory) == 0)
                        throw new ExternalException();
                    memory = 0;
                    // Capture while still holding the clipboard lock, so a
                    // different application's copy is never marked as ours.
                    return GetClipboardSequenceNumber();
                }
                finally { CloseClipboard(); }
            }
            finally { if (memory != 0) GlobalFree(memory); }
        }
        private void Open() { if (!OpenClipboard(_owner.Handle)) throw new ExternalException(); }
        public void Dispose() => _owner.Dispose();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
        [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    }
}
