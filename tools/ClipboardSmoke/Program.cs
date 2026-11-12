using System.Runtime.InteropServices;
using System.Windows.Threading;
using NekoBridge.Core;
using NekoBridge.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var peers = new[] { a, b, c };
        var sent = new List<(Guid Peer, ControlMessage Message)>();
        var logs = new List<string>();
        bool enabled = true;
        var fake = new FakeClipboard();
        using var sync = new ClipboardSync(() => enabled, logs.Add, dispatcher, () => peers,
            (peer, message, _) => { sent.Add((peer, message)); return Task.CompletedTask; }, fake);
        var captures = new List<ClipboardSync.SuccessfulContent>();
        var historyFake = new FakeClipboard();
        using var offlineHistory = new ClipboardSync(() => false, logs.Add, dispatcher, () => peers,
            (_, _, _) => throw new Exception("Unexpected send"), historyFake, historyEnabled: () => true);
        offlineHistory.ContentSucceeded += captures.Add;
        historyFake.Copy("offline-local"); offlineHistory.HandleClipboardUpdate(); Pump(40);
        Check(captures.Count == 1 && captures[0].Text == "offline-local" && captures[0].Id != Guid.Empty, "local history works without network sharing");
        historyFake.Copy("offline-local"); offlineHistory.HandleClipboardUpdate(); Pump(30);
        Check(captures.Count == 1, "history suppresses identical content");
        offlineHistory.Receive(a, "must-not-write", Guid.NewGuid()); Pump(20);
        Check(historyFake.Writes == 0 && captures.Count == 1, "disabled sharing cannot admit remote history updates");
        var restoreEntry = new NekoBridge.Windows.History.HistoryEntry(Guid.NewGuid(), "text", Guid.NewGuid(), "test", "restored", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, 0);
        var restoring = offlineHistory.RestoreHistoryAsync(restoreEntry, null);
        Pump(40);
        Check(restoring.IsCompletedSuccessfully && historyFake.Text == "restored", "history restore waits for successful write while sharing is disabled");
        fake.Copy("local-1"); sync.HandleClipboardUpdate(); Pump(40);
        Check(sent.Count == 3, "local copy broadcasts before crossing");
        for(int i=0;i<20;i++){sync.HandleClipboardUpdate();Pump(2);}
        Check(sent.Count==3&&fake.Writes==0,"Ctrl+C without changed clipboard causes no writes or repeated sends");
        var originalId = sent[0].Message.Id;
        sync.Receive(a, "local-1", originalId); Pump(30);
        Check(fake.Writes == 0 && sent.Count == 3, "original id echo suppressed");
        sent.Clear(); var remoteId = Guid.NewGuid();
        sync.Receive(a, "remote", remoteId); Pump(30);
        Check(fake.Text == "remote" && sent.Count == 2 && sent.All(x => x.Peer != a && x.Message.Id == remoteId), "remote relay preserves id");
        sync.HandleClipboardUpdate(); Pump(20);
        Check(sent.Count == 2, "own sequence suppressed");
        fake.Copy("remote"); sync.HandleClipboardUpdate(); Pump(20);
        Check(sent.Count == 2, "same-value local rewrite suppressed");
        int writes = fake.Writes;
        for (int i = 0; i < 12; i++)
        {
            sync.Receive(a, "remote", Guid.NewGuid()); Pump(5);
            fake.Copy("remote"); sync.HandleClipboardUpdate(); Pump(5);
        }
        Check(fake.Writes == writes && sent.Count == 2, "fresh-id identical remote messages neither write nor flood relays");
        sent.Clear();
        fake.Copy("B"); sync.HandleClipboardUpdate(); Pump(20);
        fake.Copy("remote"); sync.HandleClipboardUpdate(); Pump(20);
        Check(sent.Count == 6, "local A-B-A changes propagate");
        sent.Clear(); fake.Copy("unobserved-B");
        sync.Receive(a, "remote", Guid.NewGuid()); Pump(20);
        Check(fake.Text == "remote" && fake.Writes == writes + 1 && sent.Count == 2, "remote B-A applies despite previous successful A hash");
        sent.Clear(); fake.Copy("legacy-B"); sync.Receive(a, "remote"); Pump(20);
        Check(fake.Text == "remote" && sent.Count == 2, "legacy no-id B-A is not dropped by old hash");
        sent.Clear(); fake.WriteBusy = 11;
        sync.Receive(a, "failed-remote", Guid.NewGuid()); Pump(1250);
        Check(fake.Text == "remote" && sent.Count == 0, "failed remote write does not relay");
        fake.Copy("failed-remote"); sync.HandleClipboardUpdate(); Pump(20);
        Check(sent.Count == 3, "failed remote write does not poison successful-content baseline");
        logs.Clear();
        sent.Clear(); fake.Busy = 2; sync.Receive(a, "retry", Guid.NewGuid()); Pump(270);
        Check(fake.Text == "retry" && sent.Count == 2, "busy clipboard eventually succeeds");
        fake.Busy = 30; int before = fake.Attempts; sync.Receive(a, "blocked", Guid.NewGuid()); Pump(1250);
        Check(fake.Attempts - before == 11 && logs.Count == 1, "retry bounded to eleven attempts");
        fake.Busy = 1; sync.Receive(a, "stale", Guid.NewGuid()); Pump(25);
        fake.Copy("newer-local"); sync.HandleClipboardUpdate(); Pump(130);
        Check(fake.Text == "newer-local", "local copy supersedes remote retry");
        fake.Busy = 1; sync.Receive(a, "late-stale", Guid.NewGuid()); Pump(25);
        fake.Copy("late-local"); Pump(150);
        Check(fake.Text == "late-local", "sequence change wins before local notification arrives");
        fake.AfterRead = () => fake.Copy("during-read");
        sync.Receive(a, "read-stale", Guid.NewGuid()); Pump(40);
        Check(fake.Text == "during-read", "sequence change during remote equality read wins");
        fake.Copy("newer-local"); sync.HandleClipboardUpdate(); Pump(20);
        fake.Busy = 1; sync.Receive(a, "disabled", Guid.NewGuid()); Pump(25);
        enabled = false; sync.EnabledChanged(); fake.Busy = 0; Pump(140);
        Check(fake.Text == "newer-local", "disable cancels pending write");
        enabled = true; sync.EnabledChanged(); sent.Clear();
        sync.Receive(Guid.NewGuid(), "untrusted", Guid.NewGuid());
        sync.Receive(a, new string('x', 262145), Guid.NewGuid()); Pump(30);
        Check(fake.Text == "newer-local" && sent.Count == 0, "untrusted and oversized rejected");
        sync.Receive(a, "older-pending", Guid.NewGuid());
        sync.Receive(b, "latest-pending", Guid.NewGuid()); Pump(30);
        Check(fake.Text == "latest-pending", "latest remote update wins");
        sent.Clear(); writes = fake.Writes;
        sync.Receive(a, "remote-B", Guid.NewGuid()); Pump(20);
        sync.Receive(a, "latest-pending", Guid.NewGuid()); Pump(20);
        Check(fake.Writes == writes + 2 && sent.Count == 4, "remote A-B-A remains two real changes");
        fake.CopyNonText(); sync.HandleClipboardUpdate(); Pump(20);
        sent.Clear(); fake.Copy("latest-pending"); sync.HandleClipboardUpdate(); Pump(20);
        Check(sent.Count == 3, "text copied again after non-text is a new change");
        sync.EnabledChanged(); sent.Clear(); writes = fake.Writes;
        sync.Receive(a, "latest-pending", Guid.NewGuid()); Pump(20);
        Check(fake.Writes == writes && sent.Count == 2, "equal current content is never rewritten even without baseline");
        sent.Clear(); sync.Receive(a, "", Guid.NewGuid()); Pump(30);
        Check(fake.Text == "" && sent.Count == 2, "empty text supported");
        var largest = new ControlMessage { Kind = "clipboard", Id = Guid.NewGuid(), Text = new string('中', 262144) };
        Check(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Type = "message", Message = largest }).Length < 2 * 1024 * 1024, "maximum escaped text fits wire frame");
        sent.Clear();
        // Exercise an actual cyclic relay graph using only in-memory accessors.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var clips = ids.Select(_ => new FakeClipboard()).ToArray();
        var nodes = new ClipboardSync[3]; int networkSends = 0;
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            nodes[i] = new ClipboardSync(() => true, logs.Add, dispatcher,
                () => ids.Where(x => x != ids[index]).ToArray(),
                (peer, m, _) => { networkSends++; nodes[Array.IndexOf(ids, peer)].Receive(ids[index], m.Text!, m.Id); return Task.CompletedTask; }, clips[i]);
        }
        clips[0].Copy("mesh"); nodes[0].HandleClipboardUpdate(); Pump(150);
        Check(networkSends == 4 && clips.All(x => x.Text == "mesh"), "triangle relay terminates");
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = i;
            clips[i].AfterWrite = () => dispatcher.BeginInvoke(new Action(() =>
            {
                clips[index].Copy(clips[index].Text!);
                nodes[index].HandleClipboardUpdate();
            }));
        }
        int meshBefore = networkSends;
        clips[1].Copy("right-copy"); nodes[1].HandleClipboardUpdate(); Pump(180);
        Check(networkSends - meshBefore == 4 && clips.All(x => x.Text == "right-copy"), "right-origin copy with manager rewriting each received notification terminates");
        int stableSends = networkSends; int stableWrites = clips.Sum(x => x.Writes);
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < nodes.Length; i++)
            { clips[i].Copy("right-copy"); nodes[i].HandleClipboardUpdate(); }
            Pump(10);
        }
        Check(networkSends == stableSends && clips.Sum(x => x.Writes) == stableWrites, "manager rewrites on all peers produce no additional writes or sends");
        foreach (var node in nodes) node.Dispose();
        sync.Dispose(); fake.Copy("disposed"); sync.HandleClipboardUpdate(); Pump(20);
        Check(sent.Count == 0, "disposed instance does not send");
        Task rich = RichSmoke.Run(dispatcher);
        while (!rich.IsCompleted) Pump(20);
        rich.GetAwaiter().GetResult();
        Console.WriteLine("Clipboard smoke passed (fake clipboard only; no desktop clipboard accessed).");
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private sealed class FakeClipboard : ClipboardSync.IClipboard
    {
        public uint Sequence { get; private set; }
        public string? Text;
        public int Busy, Writes, Attempts, WriteBusy;
        public Action? AfterRead, AfterWrite;
        public void Copy(string text) { Text = text; Sequence++; }
        public void CopyNonText() { Text = null; Sequence++; }
        private void Try() { Attempts++; if (Busy-- > 0) throw new ExternalException(); }
        public string? Read()
        {
            Try(); string? text = Text;
            var action = AfterRead; AfterRead = null; action?.Invoke();
            return text;
        }
        public uint Write(string text)
        {
            Try(); if (WriteBusy-- > 0) throw new ExternalException();
            Writes++; Copy(text); uint sequence = Sequence;
            AfterWrite?.Invoke(); return sequence;
        }
        public void Dispose() { }
    }
}
