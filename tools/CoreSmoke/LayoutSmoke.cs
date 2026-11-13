using NekoBridge.Core;

internal static class LayoutSmoke
{
    internal static void Run()
    {
        Guid a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid indirect = Guid.NewGuid();
        var settings = new AppSettings { Id = a, Layout = [a, b] };
        var first = LayoutSync.Create(settings, [b, a, indirect]);
        int saves = 0;
        Require(LayoutSync.Apply(settings, first, () => saves++), "initial apply");
        Require(settings.Layout.Contains(indirect) && settings.Peers.Count == 0, "indirect ID retained without trust");
        Require(!LayoutSync.Apply(settings, first, () => saves++), "duplicate ignored");
        Require(saves == 1, "duplicate not persisted");
        var concurrent = first with { Editor = b, DeviceIds = [a, indirect, b] };
        Require(LayoutSync.Compare(concurrent, first) > 0, "deterministic editor tie-break");
        Require(LayoutSync.Apply(settings, concurrent, () => saves++), "concurrent winner");
        Require(!LayoutSync.Apply(settings, first, () => saves++), "loser cannot overwrite");
        Require(LayoutSync.TryDecode(LayoutSync.Encode(concurrent), out var decoded)
            && decoded.DeviceIds.SequenceEqual(concurrent.DeviceIds), "codec roundtrip");
        Require(!LayoutSync.TryDecode(new() { Kind = "layout", Payload = "{}" }, out _), "invalid payload rejected");
        Require(!LayoutSync.IsValid(first with { DeviceIds = [a, a] }), "duplicate IDs rejected");
        Require(!LayoutSync.IsValid(first with { DeviceIds = [Guid.Empty] }), "empty ID rejected");
        Require(!LayoutSync.IsValid(first with { DeviceIds = Enumerable.Range(0, 65).Select(_ => Guid.NewGuid()).ToArray() }), "64 ID bound");
        var next = LayoutSync.Create(settings, [b, indirect, a]);
        try { LayoutSync.Apply(settings, next, () => throw new IOException("expected")); throw new Exception("missing persist failure"); }
        catch (IOException) { }
        Require(settings.LayoutRevision == concurrent.Revision && settings.LayoutEditor == b
            && settings.Layout.SequenceEqual(concurrent.DeviceIds), "persist rollback");
        Require(LayoutSync.Create(settings, settings.Layout).Revision == concurrent.Revision + 1, "Lamport advances after receive");
        var nodes = new[]
        {
            new AppSettings { Id = a, Layout = [a, b, indirect] },
            new AppSettings { Id = b, Layout = [a, b, indirect] },
            new AppSettings { Id = indirect, Layout = [a, b, indirect] }
        };
        var editAtB = LayoutSync.Create(nodes[1], [indirect, b, a]);
        LayoutSync.Apply(nodes[1], editAtB, () => { });
        Require(LayoutSync.Apply(nodes[0], editAtB, () => { })
            && LayoutSync.Apply(nodes[2], editAtB, () => { }), "B edit relayed via A to C");
        Require(nodes.All(n => n.Layout.SequenceEqual(editAtB.DeviceIds)), "three-node order agrees");
        var offlineEdit = LayoutSync.Create(nodes[2], [b, a, indirect]);
        LayoutSync.Apply(nodes[2], offlineEdit, () => { });
        LayoutSync.Apply(nodes[0], offlineEdit, () => { });
        Require(!LayoutSync.Apply(nodes[0], editAtB, () => { }), "reconnect stale snapshot ignored");
        Require(LayoutSync.Apply(nodes[1], LayoutSync.Read(nodes[0]), () => { }), "reconnected peer catches up");
        Require(nodes.All(n => n.Layout.SequenceEqual(offlineEdit.DeviceIds)), "reconnect converges");
        Console.WriteLine("PASS layout codec, bounds, indirect IDs, Lamport conflict, duplicate persistence, rollback, three-node relay and reconnect");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception("Layout smoke failed: " + name);
    }
}
