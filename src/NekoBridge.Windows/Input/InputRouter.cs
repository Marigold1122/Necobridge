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
    internal void SetLayout(IEnumerable<Guid> layout)
    {
        lock(_gate)
        {
            var ids=layout.Distinct().Where(id=>id==_node.LocalId||_node.Peers.Any(p=>p.Id==id&&p.IsConnected)).ToList();
            if(!ids.Contains(_node.LocalId))ids.Insert(0,_node.LocalId);
            _layout=ids.ToArray();
            if(!_layout.Contains(_active)) ReturnLocalLocked();
        }
    }
    internal void SetPaused(bool value)
    {
        lock(_gate){_paused=value;if(value){ReturnLocalLocked();ReleaseReceivedLocked();_owner=Guid.Empty;}}
    }
    internal static bool IsReceiverReplay(PhysicalInput input,bool hasRemoteOwner)
        => hasRemoteOwner && input.SourceInjected;

    internal static bool IsLocalTakeover(PhysicalInput input,bool hasRemoteOwner)
        => hasRemoteOwner&&!input.SourceInjected
           &&(input.Kind is "move" or "key" or "wheel" || input.Kind=="button"&&input.Flags==0);
    internal static bool ShouldRecoverRawRelease(bool forwardingToRemote)=>forwardingToRemote;

    private bool HandlePhysical(PhysicalInput e)
    {
        lock(_gate)
        {
            if(_disposed)return false;
            if(e.Kind=="rawButtonUp")
            {
                // Local Windows already receives the physical UP. Replaying it
                // from Raw Input duplicates releases and can dismiss tray flyouts.
                if(!ShouldRecoverRawRelease(_active!=_node.LocalId))return false;
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
                if(e.Flags==0)_physicalButtons.Add(e.Data);else if(!_physicalButtons.Remove(e.Data))return _active!=_node.LocalId;
                if(e.Data==1)
                {
                    _leftDown=e.Flags==0;
                    if(!_leftDown)_dragCrossingBlocked=false;
                    PhysicalLeftChanged?.Invoke(_leftDown);
                }
            }
            if(_paused)return false;
            if(_parkController!=Guid.Empty && !e.SourceInjected)RestoreParking();
            if(_dragStarting&&e.Kind=="move")return true;
            if(_active==_node.LocalId)
            {
                if(e.Kind=="move"&&!_edgePending&&!_dragCrossingBlocked)
                {
                    int direction=e.X<=Left ? -1:e.X>=Left+Width-1 ? 1:0;
                    if(direction!=0)
                    {
                        Guid next=Neighbor(direction);
                        if(next!=Guid.Empty)
                        {
                            _edgePending=true;
                            var size=Size(next);
                            int nx=direction>0?Math.Min(2,size.W-1):Math.Max(0,size.W-3);
                            int ny=MapVertical(e.Y-Top,Height,size.H);
                            if(_leftDown)EdgeCrossing?.Invoke(_active,next,nx,ny,true);
                            else {CompleteCrossing(next,nx,ny);return true;}
                        }
                    }
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
                _y=Math.Clamp(_y+NormalizePointerDelta((double)e.Y-parked.Y,Height,_targetHeight),0,_targetHeight-1);
                int direction=nextX<0?-1:nextX>_targetWidth-1?1:0;
                if(direction!=0&&!_edgePending&&!_dragCrossingBlocked)
                {
                    Guid next=Neighbor(direction);
                    if(next!=Guid.Empty)
                    {
                        _edgePending=true;
                        var ns=Size(next);
                        int nx=direction>0?Math.Min(2,ns.W-1):Math.Max(0,ns.W-3);
                        int ny=MapVertical(_y,_targetHeight,ns.H);
                        if(_leftDown)EdgeCrossing?.Invoke(_active,next,nx,ny,true);
                        else {CompleteCrossing(next,nx,ny);return true;}
                    }
                }
                _x=Math.Clamp(nextX,0,_targetWidth-1);
                _move=(_active,new ControlMessage{Kind="move",X=(int)Math.Round(_x),Y=(int)Math.Round(_y)});
                return true;
            }
            FlushMoveLocked();
            Queue(_active,new ControlMessage{Kind=e.Kind,X=e.X,Y=e.Y,Data=e.Data,Flags=e.Flags});
            return true;
        }
    }
    private Guid Neighbor(int direction)
    {
        int index=Array.IndexOf(_layout,_active)+direction;
        return index>=0&&index<_layout.Length?_layout[index]:Guid.Empty;
    }
    private (int W,int H) Size(Guid id)
    {
        if(id==_node.LocalId)return(Width,Height);
        var p=_node.Peers.FirstOrDefault(p=>p.Id==id);
        return(Math.Max(1,p?.Width??Width),Math.Max(1,p?.Height??Height));
    }
    internal static double NormalizePointerDelta(double delta,int sourceExtent,int targetExtent)
        => delta/Math.Max(1,sourceExtent)*Math.Max(1,targetExtent);

    private static int MapVertical(double y,int sourceHeight,int targetHeight)
        => (int)Math.Round(Math.Clamp(y/Math.Max(1,sourceHeight-1),0,1)*(targetHeight-1));

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
        int x=bounds.Left+bounds.Width/2,y=bounds.Top;
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
