using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NekoBridge.Core;

namespace NekoBridge.Windows;

public partial class MainWindow
{
    private readonly HashSet<Guid> _layoutConnected = new();

    // Called on the dispatcher, before the original RefreshPeers routing/screen projection.
    private List<Guid> SyncOnPeers(PeerSnapshot[] peers)
    {
        var connected = peers.Where(p => p.IsConnected).Select(p => p.Id).ToHashSet();
        var newlyConnected = connected.Except(_layoutConnected).ToArray();
        _layoutConnected.Clear();
        _layoutConnected.UnionWith(connected);

        var saved = _state.Settings.Layout;
        var merged = saved.Where(id => id != Guid.Empty).Distinct().ToList();
        foreach (var id in peers.Select(p => p.Id).Prepend(_node.LocalId))
            if (!merged.Contains(id)) merged.Add(id);
        if (merged.Count > LayoutSync.MaxDevices)
            throw new InvalidDataException("共享屏幕排列超过 64 台设备，未截断或覆盖原排列。");

        var current = LayoutSync.Read(_state.Settings);
        bool changed = !saved.SequenceEqual(merged) || !LayoutSync.IsValid(current);
        if (changed)
        {
            var snapshot = LayoutSync.Create(_state.Settings, merged);
            bool orderChanged = !saved.SequenceEqual(merged);
            LayoutSync.Apply(_state.Settings, snapshot, _state.Save);
            if (orderChanged) { _router.ReturnLocal(); CancelDrag(); }
            BroadcastLayout(snapshot);
        }
        else
            foreach (var peer in newlyConnected) SendObserved(peer, LayoutSync.Encode(current));
        return _state.Settings.Layout.ToList();
    }

    // Caller must copy the saved list before applying the existing RemoveAt/Insert drag semantics.
    private void CommitSharedLayout(IEnumerable<Guid> list)
    {
        var order = list.ToArray();
        if (_state.Settings.Layout.SequenceEqual(order)) return;
        var snapshot = LayoutSync.Create(_state.Settings, order);
        LayoutSync.Apply(_state.Settings, snapshot, _state.Save);
        _router.ReturnLocal(); CancelDrag();
        RefreshPeers();
        BroadcastLayout(snapshot);
    }

    private void ReceiveLayout(Guid peer, ControlMessage message)
    {
        if (_quitting || !_node.Peers.Any(p => p.Id == peer && p.IsConnected)) return;
        if (!LayoutSync.TryDecode(message, out var snapshot))
            throw new InvalidDataException("远端共享屏幕排列无效。");
        bool orderChanged = !_state.Settings.Layout.SequenceEqual(snapshot.DeviceIds);
        if (!LayoutSync.Apply(_state.Settings, snapshot, _state.Save)) return;
        if (orderChanged) { _router.ReturnLocal(); CancelDrag(); }
        // Relay accepted versions, never back to the source. Duplicate/stale versions stop here.
        BroadcastLayout(snapshot, peer);
        RefreshPeers();
    }

    private void BroadcastLayout(LayoutSnapshot snapshot, Guid except = default)
    {
        var message = LayoutSync.Encode(snapshot);
        foreach (var peer in _node.Peers.Where(p => p.IsConnected && p.Id != except))
            SendObserved(peer.Id, message);
    }
}
