using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using NekoBridge.Core;

namespace NekoBridge.Windows;
public sealed class PeerCard:INotifyPropertyChanged
{
    public Guid Id{get;init;}
    public string Name{get;private set;}="";
    public string Address{get;private set;}="";
    public bool Online{get;private set;}
    private bool _busy;private string _error="";
    public bool Busy{get=>_busy;set{_busy=value;Changed();Changed(nameof(CanOperate));Changed(nameof(Status));}}
    public bool CanOperate=>!Busy;
    public string Status=>Busy?"连接中":Online?"已连接":"未连接";
    public string Error{get=>_error;set{_error=value;Changed();}}
    public void Update(PeerSnapshot snapshot){Name=snapshot.DisplayName;Address=snapshot.Address;Online=snapshot.IsConnected;Changed(nameof(Name));Changed(nameof(Address));Changed(nameof(Status));}
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
public partial class MainWindow
{
    private readonly Dictionary<Guid,PeerCard> _peerCards=new();
    private readonly HashSet<Guid> _peerRequests=new();
    private bool _guideOpenedPairing;
    private void RefreshPeerCards(PeerSnapshot[] peers)
    {
        foreach(var id in _peerCards.Keys.Where(id=>!peers.Any(p=>p.Id==id)&&!_peerRequests.Contains(id)).ToArray())_peerCards.Remove(id);
        foreach(var peer in peers)
        {
            if(!_peerCards.TryGetValue(peer.Id,out var card))_peerCards.Add(peer.Id,card=new(){Id=peer.Id});
            card.Update(peer);
        }
        PeerList.ItemsSource=peers.Select(p=>_peerCards[p.Id]).ToArray();
    }
    private void CancelGuidePairing()
    {
        _guideConnectStop?.Cancel();_guideConnectStop=null;_connecting=false;
        if(_guideOpenedPairing){_node.PairingEnabled=false;_guideOpenedPairing=false;_pairingStarted=null;}
        JoinCode.Clear();ConnectionFeedback.Text="";
    }
    private void ReturnDevices_Click(object sender,RoutedEventArgs e)
    {
        if(_connectionTransition)return;
        CancelGuidePairing();_connectionPrepared=false;_connectionSucceeded=false;_connectionController=null;
        _connectionOverviewRequested=_node.Peers.Count>0;
        ShowConnectionView(_node.Peers.Count>0?ConnectionOverview:ConnectionIntro);RefreshStatus();
    }
    private async void ReconnectPeer_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement{DataContext:PeerCard card}||!_started||!_peerRequests.Add(card.Id))return;
        card.Busy=true;card.Error="";
        try
        {
            var peer=_node.Peers.FirstOrDefault(p=>p.Id==card.Id);
            if(peer==null)throw new InvalidOperationException("设备已被移除。");
            // Dial/register replaces the old authenticated channel only after
            // the new connection succeeds. Do not revoke trust to reconnect.
            await _node.ConnectAsync(peer.Address,null,_lifetime.Token);
        }
        catch(OperationCanceledException)when(_quitting) { }
        catch(Exception ex){card.Error=$"连接失败：{ex.Message}";Log(card.Error);}
        finally
        {
            _peerRequests.Remove(card.Id);card.Busy=false;
            if(!_quitting)RefreshPeers();
        }
    }
}
