using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NekoBridge.Core;

namespace NekoBridge.Windows;
public partial class MainWindow
{
    private readonly HashSet<Guid> _layoutConnected=new();
    private LayoutCell[] SyncOnPeers(PeerSnapshot[] peers)
    {
        var connected=peers.Where(p=>p.IsConnected).Select(p=>p.Id).ToHashSet();
        var newlyConnected=connected.Except(_layoutConnected).ToArray();
        var current=LayoutSync.Read(_state.Settings);
        var cells=LayoutSync.Positions(current).ToList();
        foreach(var id in peers.Select(p=>p.Id).Prepend(_node.LocalId))
            if(!cells.Any(c=>c.Id==id))cells.Add(new(id,cells.Count==0?0:cells.Max(c=>c.Column)+1,0));
        if(cells.Count>LayoutSync.MaxDevices)throw new InvalidDataException("共享排列超过64台设备。");
        if(!LayoutSync.IsValid(current)||current.Cells==null||!cells.SequenceEqual(current.Cells))
        {
            var next=LayoutSync.CreateCells(_state.Settings,cells);
            bool changed=!LayoutSync.SamePositions(current,next);
            LayoutSync.Apply(_state.Settings,next,_state.Save);
            if(changed){_router.ReturnLocal();CancelDrag();}
            BroadcastLayout(next);
        }
        else foreach(var id in newlyConnected)SendObserved(id,LayoutSync.Encode(current));
        _layoutConnected.Clear();_layoutConnected.UnionWith(connected);
        return _state.Settings.LayoutCells.ToArray();
    }
    private void CommitSharedLayout(IEnumerable<LayoutCell> cells)
    {
        var snapshot=LayoutSync.CreateCells(_state.Settings,cells);
        if(LayoutSync.SamePositions(LayoutSync.Read(_state.Settings),snapshot))return;
        LayoutSync.Apply(_state.Settings,snapshot,_state.Save);
        _router.ReturnLocal();CancelDrag();RefreshPeers();BroadcastLayout(snapshot);
    }
    private void ReceiveLayout(Guid peer,ControlMessage message)
    {
        if(_quitting||!_node.Peers.Any(p=>p.Id==peer&&p.IsConnected))return;
        if(!LayoutSync.TryDecode(message,out var snapshot))throw new InvalidDataException("远端二维排列无效。");
        bool changed=!LayoutSync.SamePositions(LayoutSync.Read(_state.Settings),snapshot);
        if(!LayoutSync.Apply(_state.Settings,snapshot,_state.Save))return;
        if(changed){_router.ReturnLocal();CancelDrag();}
        BroadcastLayout(snapshot,peer);RefreshPeers();
    }
    private void BroadcastLayout(LayoutSnapshot snapshot,Guid except=default)
    {
        foreach(var peer in _node.Peers.Where(p=>p.IsConnected&&p.Id!=except))SendObserved(peer.Id,LayoutSync.Encode(snapshot));
    }
}
