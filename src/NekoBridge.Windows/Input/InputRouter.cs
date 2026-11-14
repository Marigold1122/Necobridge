using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NekoBridge.Core;

namespace NekoBridge.Windows.Input;

internal sealed class InputRouter : IDisposable
{
    private readonly BridgeNode _node;
    private readonly Action<string> _log;
    private readonly object _gate=new();
    private readonly Channel<(Guid Peer,ControlMessage Message)> _outbox=Channel.CreateBounded<(Guid,ControlMessage)>(new BoundedChannelOptions(512){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly CancellationTokenSource _stop=new();
    private readonly InputHooks _hooks;
    private readonly System.Threading.Timer _timer;
    private readonly Task _sender;
    private readonly HashSet<int> _receivedKeys=new();
    private readonly HashSet<int> _receivedButtons=new();
    private readonly Dictionary<int,(int Scan,bool Extended)> _physicalKeys=new();
    private readonly HashSet<int> _physicalButtons=new();
    private long _generation;
    internal long Generation {get{lock(_gate)return _generation;}}
    private Guid[] _layout=Array.Empty<Guid>();
    private LayoutCell[] _cells=Array.Empty<LayoutCell>();
    private Guid _active;
    private Guid _owner;
    private DateTime _lastIncoming;
    private double _x,_y;
    private int _targetWidth,_targetHeight;
    private bool _leftDown;
    private bool _edgePending;
    private bool _dragStarting;
    private bool _dragCrossingBlocked;
    private NativeMethods.Point? _localReturnPoint;
    private bool _paused;
    private bool _disposed;
    private bool _reportedReceiverReplay;
    private (Guid Peer,ControlMessage Message)? _move;
    private long _lastKeepAlive;
    private readonly CursorParking _parking;
    private Guid _parkController;
    private DateTime _parkHeartbeat;
    internal event Action<Guid,Guid,int,int,bool>? EdgeCrossing;
    internal event Action<Guid>? ActiveChanged;
    internal event Action<bool>? RemoteLeftChanged;
    internal event Action<bool>? PhysicalLeftChanged;
    internal event Action? CancelDragRequested;
    internal event Action? EmergencyReturned;
    internal int Left=>NativeMethods.GetSystemMetrics(76);
    internal int Top=>NativeMethods.GetSystemMetrics(77);
    internal int Width=>Math.Max(1,NativeMethods.GetSystemMetrics(78));
    internal int Height=>Math.Max(1,NativeMethods.GetSystemMetrics(79));
    internal Guid ActiveId { get {lock(_gate)return _active;} }
    internal Guid OwnerId { get {lock(_gate)return _owner;} }
    internal bool LeftDown { get {lock(_gate)return _leftDown;} }
    internal bool Paused {get{lock(_gate)return _paused;}}
    internal Task Ready=>_hooks.Ready;
    internal InputRouter(BridgeNode node,Action<string> log)
    {
        _node=node;_log=log;_active=node.LocalId;
        _parking=new CursorParking(log);
        _hooks=new InputHooks(HandlePhysical,log);
        _sender=Task.Run(SendLoopAsync);
        _timer=new System.Threading.Timer(Tick,null,8,8);
    }
    internal void SetLayout(IEnumerable<Guid> layout)=>SetLayout(layout.Select((id,i)=>new LayoutCell(id,i,0)));
    internal void SetLayout(IEnumerable<LayoutCell> layout)
    {
        lock(_gate)
        {
            var cells=layout.ToArray();
            if(!LayoutSync.ValidCells(cells)||!cells.Any(c=>c.Id==_node.LocalId))throw new ArgumentException("本机屏幕位置无效。");
            _cells=cells;
            _layout=cells.Where(c=>c.Id==_node.LocalId||_node.Peers.Any(p=>p.Id==c.Id&&p.IsConnected)).Select(c=>c.Id).ToArray();
            if(!_layout.Contains(_active))ReturnLocalLocked();
        }
    }
    internal void SetPaused(bool value)
    {
        lock(_gate){_paused=value;if(value){ReturnLocalLocked();ReleaseReceivedLocked();_owner=Guid.Empty;}}
    }
    internal static bool IsParkedReplay(PhysicalInput input,bool parked)
        => parked&&input.SourceInjected&&input.Kind=="move";

    internal static bool IsReceiverReplay(PhysicalInput input,bool hasRemoteOwner)
        => hasRemoteOwner && input.SourceInjected;

    internal static bool IsLocalTakeover(PhysicalInput input,bool hasRemoteOwner)
        => hasRemoteOwner&&!input.SourceInjected
           &&(input.Kind is "move" or "key" or "wheel" || input.Kind=="button"&&input.Flags==0);
    internal static bool RecordButton(HashSet<int> held,int button,bool up)
        => up?held.Remove(button):held.Add(button);

    private void ObserveLeftButton(PhysicalInput input)
    {
        if(input.Data!=1)return;
        _leftDown=input.Flags==0;
        if(!_leftDown)_dragCrossingBlocked=false;
        PhysicalLeftChanged?.Invoke(_leftDown);
    }

    private bool HandlePhysical(PhysicalInput e)
    {
        lock(_gate)
        {
            if(_disposed)return false;
            // A receiver has no remote owner after leave, but must still reject
            // software-replayed moves until a real local input or enter restores it.
            // Our own marked parking/enter injections bypass this hook entirely.
            if(IsParkedReplay(e,_parkController!=Guid.Empty))return true;
            if(e.Kind=="rawButtonUp")
            {
                // Raw UP is authoritative for held-state bookkeeping even when
                // a gesture hook consumed the ordinary UP. Local delivery must
                // not inject a second UP into Windows.
                if(_active==_node.LocalId)
                {
                    if(RecordButton(_physicalButtons,e.Data,true))ObserveLeftButton(e);
                    return false;
                }
                if(!_physicalButtons.Contains(e.Data))return false;
                e=e with {Kind="button"};
            }
            // Receiver-side gesture tools may replay our input with a different marker.
            if(IsReceiverReplay(e,_owner!=Guid.Empty))
            {
                if(!_reportedReceiverReplay)
                {
                    _reportedReceiverReplay=true;
                    _log($"输入诊断：接收端软件事件保持远端控制，kind={e.Kind}, injected=true, extra=0x{e.SourceExtraInfo:X}。");
                }
                return false;
            }
            if(e.Kind=="emergency")
            {
                _leftDown=false;ReturnLocalLocked();ReleaseReceivedLocked();_owner=Guid.Empty;
                EmergencyReturned?.Invoke();return false;
            }
            if(!_paused&&IsLocalTakeover(e,_owner!=Guid.Empty))
            {
                var owner=_owner;_owner=Guid.Empty;
                RestoreParking();
                Queue(owner,new ControlMessage{Kind="yield"});
                CancelDragRequested?.Invoke();
                // The physical DOWN already establishes this button's state;
                // injecting a remote UP here would cancel that very first click.
                if(e.Kind=="button"&&e.Flags==0)_receivedButtons.Remove(e.Data);
                if(e.Kind=="key"&&(e.Flags&1)==0)_receivedKeys.Remove(e.X);
                ReleaseReceivedLocked();
                ActiveChanged?.Invoke(_node.LocalId);
            }
            if(e.Kind=="key")
            {
                if(e.X==0x1B&&e.Flags==0)CancelDragRequested?.Invoke();
                if((e.Flags&1)==0)_physicalKeys[e.X]=(e.Y,(e.Flags&2)!=0);else _physicalKeys.Remove(e.X);
            }
            if(e.Kind=="button")
            {
                bool changed=RecordButton(_physicalButtons,e.Data,e.Flags!=0);
                if(e.Flags!=0&&!changed)return _active!=_node.LocalId;
                ObserveLeftButton(e);
            }
            if(_paused)return false;
            if(_parkController!=Guid.Empty && !e.SourceInjected)
            {
                _log($"光标隐藏结束：本机输入 {e.Kind}。");RestoreParking();
            }
            if(_dragStarting&&e.Kind=="move")return true;
            if(_active==_node.LocalId)
            {
                if(e.Kind=="move"&&!_edgePending&&!_dragCrossingBlocked)
                {
                    var side=DetectSide(e.X-Left,e.Y-Top,Width,Height,true);
                    if(TryCross(side,e.X-Left,e.Y-Top,Width,Height))return true;
                }
                return false;
            }
            if(e.Kind=="move")
            {
                if(_edgePending&&_leftDown)return true;
                // Mouse Without Borders default absolute mode (MIT), PowerToys
                // f5f29e08b39cc7139ccbe4a2ec4eb4841779bc45: InputHook.MouseHookProc
                // uses hook.pt - actual parked cursor, NOT raw device counts.
                // Event.MouseEvent normalizes each axis by its source screen extent.
                // Accumulating in destination pixels is equivalent, retaining fractions.
                // Read the actual cursor each time so injected parking/return moves do
                // not become physical deltas. Suppression keeps the top-edge park fixed.
                if(!NativeMethods.GetCursorPos(out var parked))
                {
                    _log("无法读取本机光标位置，已停止远程控制。");
                    ReturnLocalLocked();return false;
                }
                double nextX=_x+NormalizePointerDelta((double)e.X-parked.X,Width,_targetWidth);
                double nextY=_y+NormalizePointerDelta((double)e.Y-parked.Y,Height,_targetHeight);
                var side=DetectSide(nextX,nextY,_targetWidth,_targetHeight,false);
                if(!_edgePending&&!_dragCrossingBlocked&&TryCross(side,nextX,nextY,_targetWidth,_targetHeight))return true;
                _x=Math.Clamp(nextX,0,_targetWidth-1);
                _y=Math.Clamp(nextY,0,_targetHeight-1);
                _move=(_active,new ControlMessage{Kind="move",X=(int)Math.Round(_x),Y=(int)Math.Round(_y)});
                return true;
            }
            FlushMoveLocked();
            Queue(_active,new ControlMessage{Kind=e.Kind,X=e.X,Y=e.Y,Data=e.Data,Flags=e.Flags});
            return true;
        }
    }
    internal static LayoutSide DetectSide(double x,double y,int width,int height,bool local)
    {
        if(local?x<=0:x<0)return LayoutSide.Left;
        if(local?x>=width-1:x>width-1)return LayoutSide.Right;
        if(local?y<=0:y<0)return LayoutSide.Up;
        if(local?y>=height-1:y>height-1)return LayoutSide.Down;
        return LayoutSide.None;
    }
    private bool TryCross(LayoutSide side,double x,double y,int width,int height)
    {
        var next=LayoutSync.Neighbor(_cells,_active,side);
        if(next==Guid.Empty||!_layout.Contains(next))return false;
        var size=Size(next);var entry=LayoutSync.Entry(side,x,y,width,height,size.W,size.H);
        _edgePending=true;
        if(_leftDown)EdgeCrossing?.Invoke(_active,next,entry.X,entry.Y,true);
        else if(!CompleteCrossing(next,entry.X,entry.Y))_edgePending=false;
        return true;
    }
    private (int W,int H) Size(Guid id)
    {
        if(id==_node.LocalId)return(Width,Height);
        var p=_node.Peers.FirstOrDefault(p=>p.Id==id);
        return(Math.Max(1,p?.Width??Width),Math.Max(1,p?.Height??Height));
    }
    internal static double NormalizePointerDelta(double delta,int sourceExtent,int targetExtent)
        => delta/Math.Max(1,sourceExtent)*Math.Max(1,targetExtent);

    internal bool CompleteCrossing(Guid target,int x,int y,bool nativeDrag=false,long expectedGeneration=-1,ControlMessage? dragCommit=null,Action? beforeDragSwitch=null)
    {
        lock(_gate)
        {
            if(_paused || !_layout.Contains(target) || (nativeDrag&&!_leftDown) || (expectedGeneration>=0&&expectedGeneration!=_generation))return false;
            beforeDragSwitch?.Invoke();
            FlushMoveLocked();
            bool leavingLocal=_active==_node.LocalId;
            if(!leavingLocal)Queue(_active,new ControlMessage{Kind="leave",Flags=1});
            else
            {
                if(NativeMethods.GetCursorPos(out var point))_localReturnPoint=point;
                foreach(var key in _physicalKeys)NativeMethods.InjectKey(key.Key,key.Value.Scan,true,key.Value.Extended);
                foreach(int button in _physicalButtons)InjectButton(button,true);
            }
            (_targetWidth,_targetHeight)=Size(target);
            _dragStarting=nativeDrag;
            _active=target;_x=x;_y=y;_edgePending=false;
            if(target==_node.LocalId)
            {
                RestoreParking();
                NativeMethods.MoveCursor(Left+x,Top+y);
                _localReturnPoint=null;
                foreach(var key in _physicalKeys)NativeMethods.InjectKey(key.Key,key.Value.Scan,false,key.Value.Extended);
                foreach(int button in _physicalButtons)if(!nativeDrag||button!=1)InjectButton(button,false);
            }
            else
            {
                Queue(target,new ControlMessage{Kind="enter",X=x,Y=y});
                if(dragCommit!=null)Queue(target,dragCommit);
                foreach(var key in _physicalKeys)Queue(target,new ControlMessage{Kind="key",X=key.Key,Y=key.Value.Scan,Flags=key.Value.Extended?2:0});
                foreach(int button in _physicalButtons)if(!nativeDrag||button!=1)Queue(target,new ControlMessage{Kind="button",Data=button});
                if(leavingLocal)ParkCursor();
            }
            ActiveChanged?.Invoke(target);return true;
        }
    }
    private void ParkCursor()
    {
        var screen=System.Windows.Forms.Screen.AllScreens.OrderBy(s=>s.Bounds.Top).ThenByDescending(s=>s.Primary).FirstOrDefault();
        if(screen is null)return;
        var bounds=screen.Bounds;
        int x=bounds.Left+bounds.Width/2,y=bounds.Top+Math.Min(4,bounds.Height-1);
        _parking.Show(x,y);
        try{NativeMethods.MoveCursor(x,y);}
        catch(System.ComponentModel.Win32Exception ex){RestoreParking();_log($"无法停放光标：{ex.Message}");}
    }
    private void RestoreParking(){_parkController=Guid.Empty;_parking.Hide();}
    internal void DragStarted(){lock(_gate)_dragStarting=false;}
    internal void CancelCrossing(){lock(_gate){_generation++;_edgePending=false;_dragStarting=false;_dragCrossingBlocked=_leftDown;}}
    internal void ReturnLocal(){lock(_gate)ReturnLocalLocked();}
    private void ReturnLocalLocked(bool notifyPeer=true)
    {
        _generation++;
        RestoreParking();
        bool returning=_active!=_node.LocalId;
        if(returning&&notifyPeer)_outbox.Writer.TryWrite((_active,new ControlMessage{Kind="leave"}));
        _active=_node.LocalId;_edgePending=false;_dragStarting=false;_move=null;_dragCrossingBlocked=_leftDown;
        CancelDragRequested?.Invoke();
        if(returning&&_localReturnPoint is {} point)
        {
            try{NativeMethods.MoveCursor(Math.Clamp(point.X,Left,Left+Width-1),Math.Clamp(point.Y,Top,Top+Height-1));}
            catch(System.ComponentModel.Win32Exception ex){_log($"无法恢复本机光标位置：{ex.Message}");}
        }
        _localReturnPoint=null;
        ActiveChanged?.Invoke(_active);
    }
    internal void ReleaseSourceDrag()
    {
        NativeMethods.InjectKey(0x1B,0,false,false);
        NativeMethods.InjectKey(0x1B,0,true,false);
        NativeMethods.InjectMouse(0,0,4);
    }
    internal void Accept(Guid peer,ControlMessage m)
    {
        lock(_gate)
        {
            if(m.Kind=="yield") { if(_active==peer)ReturnLocalLocked(); return; }
            if(_paused)return;
            if(m.Kind=="keepalive"&&_parkController==peer){_parkHeartbeat=DateTime.UtcNow;return;}
            if(m.Kind=="enter")
            {
                if(_owner!=Guid.Empty&&_owner!=peer) {Queue(peer,new ControlMessage{Kind="yield"});return;}
                if(_active!=_node.LocalId)ReturnLocalLocked();
                if(_owner!=peer)_reportedReceiverReplay=false;
                RestoreParking();
                _owner=peer;_lastIncoming=DateTime.UtcNow;
                NativeMethods.MoveCursor(Left+Math.Clamp(m.X,0,Width-1),Top+Math.Clamp(m.Y,0,Height-1));
                return;
            }
            if(_owner!=peer)return;
            _lastIncoming=DateTime.UtcNow;
            switch(m.Kind)
            {
                case "keepalive":break;
                case "leave":
                    CancelDragRequested?.Invoke();ReleaseReceivedLocked();_owner=Guid.Empty;
                    _parkController=peer;_parkHeartbeat=DateTime.UtcNow;ParkCursor();
                    _log("收到离开消息：被控端进入光标隐藏状态。");
                    break;
                case "move":NativeMethods.MoveCursor(Left+Math.Clamp(m.X,0,Width-1),Top+Math.Clamp(m.Y,0,Height-1));break;
                case "key":
                    if(m.X is <1 or >254||m.Y is <0 or >65535||m.Flags is <0 or >3)throw new InvalidOperationException("远端键盘事件无效");
                    if((m.Flags&1)==0)_receivedKeys.Add(m.X);else _receivedKeys.Remove(m.X);
                    NativeMethods.InjectKey(m.X,m.Y,(m.Flags&1)!=0,(m.Flags&2)!=0);break;
                case "button":
                    if(m.Data is <1 or >5||m.Flags is <0 or >1)throw new InvalidOperationException("远端鼠标事件无效");
                    bool up=m.Flags==1;
                    if(up)_receivedButtons.Remove(m.Data);else _receivedButtons.Add(m.Data);
                    try{InjectButton(m.Data,up);}
                    finally{if(m.Data==1)RemoteLeftChanged?.Invoke(!up);}
                    break;
                case "wheel":if(m.Data is >=-32768 and <=32767)NativeMethods.InjectMouse(0,0,m.Flags==1?0x1000u:0x800u,unchecked((uint)m.Data));break;
            }
        }
    }
    private static void InjectButton(int b,bool up)
    {
        uint flags=b switch{1=>up?4u:2u,2=>up?16u:8u,3=>up?64u:32u,_=>up?256u:128u};
        NativeMethods.InjectMouse(0,0,flags,b>=4?(uint)(b-3):0);
    }
    private void ReleaseReceivedLocked()
    {
        foreach(int key in _receivedKeys.ToArray())
        {
            try{NativeMethods.InjectKey(key,0,true,key is 0xA3 or 0xA5 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22);}catch(Exception ex){_log(ex.Message);}
        }
        foreach(int button in _receivedButtons.ToArray()){try{InjectButton(button,true);}catch(Exception ex){_log(ex.Message);}}
        _receivedKeys.Clear();_receivedButtons.Clear();RemoteLeftChanged?.Invoke(false);
    }
    internal void SendOrdered(Guid peer,ControlMessage message){lock(_gate){FlushMoveLocked();Queue(peer,message);}}
    private void Queue(Guid peer,ControlMessage message)
    {
        if(!_outbox.Writer.TryWrite((peer,message)))
        {
            _log("输入队列已满，已停止远程控制以避免粘键。");
            ReturnLocalLocked(notifyPeer:false);
        }
    }
    private void FlushMoveLocked(){if(_move is {} m){Queue(m.Peer,m.Message);_move=null;}}
    private void Tick(object? state)
    {
        lock(_gate)
        {
            if(_disposed)return;
            FlushMoveLocked();
            long now=Environment.TickCount64;
            if(!_paused&&now-_lastKeepAlive>400)
            {
                foreach(var peer in _node.Peers.Where(p=>p.IsConnected))Queue(peer.Id,new ControlMessage{Kind="keepalive"});
                _lastKeepAlive=now;
            }
            if(_parkController!=Guid.Empty&&((DateTime.UtcNow-_parkHeartbeat).TotalSeconds>2||!_node.Peers.Any(p=>p.Id==_parkController&&p.IsConnected)))RestoreParking();
            if(_active!=_node.LocalId&&!_node.Peers.Any(p=>p.Id==_active&&p.IsConnected))ReturnLocalLocked();
            if(_owner!=Guid.Empty&&((DateTime.UtcNow-_lastIncoming).TotalSeconds>2||!_node.Peers.Any(p=>p.Id==_owner&&p.IsConnected)))
            {
                CancelDragRequested?.Invoke();ReleaseReceivedLocked();_owner=Guid.Empty;RestoreParking();_log("输入心跳超时，已释放所有远端按键。");
            }
        }
    }
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach(var item in _outbox.Reader.ReadAllAsync(_stop.Token))
            {
                try{await _node.SendAsync(item.Peer,item.Message,_stop.Token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(_stop.IsCancellationRequested){break;}
                catch(Exception ex)
                {
                    lock(_gate){if(_active==item.Peer)ReturnLocalLocked();}
                    _log($"控制连接失败：{ex.Message}");
                }
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    public void Dispose()
    {
        lock(_gate){if(_disposed)return;ReturnLocalLocked();ReleaseReceivedLocked();_disposed=true;}
        _timer.Dispose();_hooks.Dispose();_parking.Dispose();_outbox.Writer.TryComplete();
        _stop.Cancel();
        try{_sender.GetAwaiter().GetResult();}catch(OperationCanceledException){}
        _stop.Dispose();
    }
}
