using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NekoBridge.Windows.History;
using NekoBridge.Core;
using NekoBridge.Windows.DragDrop;
using NekoBridge.Windows.Input;
using Forms = System.Windows.Forms;

namespace NekoBridge.Windows;

public partial class MainWindow : Window
{
    private readonly LocalState _state;
    private readonly BridgeNode _node;
    private readonly InputRouter _router;
    private readonly NativeDragBridge _drag;
    private readonly ClipboardSync _clipboard;
    private readonly HistoryStore _historyStore;
    private readonly HistoryBridge _historyBridge;
    private HistoryHotkey? _historyHotkey;
    private HistoryWindow? _historyWindow;
    private readonly ObservableCollection<string> _logs=new();
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _pairingTimer;
    private CancellationTokenSource? _crossStop;
    private CancellationTokenSource? _sourceCaptureStop;
    private bool _crossCommitted;
    private readonly Dictionary<(Guid,Guid,string),TaskCompletionSource<ControlMessage>> _dragReplies=new();
    private IncomingDrag? _incoming;
    private Guid _targetSession;
    private (Guid Peer,Guid Session,Guid Offer) _sourceCapture;
    private sealed class IncomingDrag(Guid peer,Guid id,CancellationToken lifetime)
    {
        internal readonly Guid Peer=peer, Id=id;
        internal readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal readonly TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Commit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? CancelReason { get; private set; }
        private bool _closed;
        internal void Cancel(string reason)
        {
            if(_closed)return;
            CancelReason??=reason;
            Stop.Cancel();
        }
        internal void Close(){_closed=true;Stop.Dispose();}
    }
    private bool _initialized,_quitting,_busyCrossing,_uiReady,_started;
    private string? _startupError;
    private long? _lastWarningAt;
    private Guid _screenDrag;
    private Guid _logicalSource;
    private Guid _logicalOffer;
    private IReadOnlyList<string>? _logicalPaths;
    private Guid _currentDragTarget;
    private HwndSource? _hwnd;
    private readonly int _listeningPort;

    public MainWindow()
    {
        InitializeComponent();
        Loaded+=(_,_)=>UpdateDeviceWallpaper();
        Activated+=(_,_)=>UpdateDeviceWallpaper();
        _state=new LocalState();_listeningPort=_state.Settings.Port;
        _node=new BridgeNode(_state.Settings,_state.Identity,_state.Save);
        _router=new InputRouter(_node,Log);
        _node.LocalDevice=new DeviceDescriptor(_state.Settings.Id,_state.Settings.DeviceName,_router.Width,_router.Height);
        _drag=new NativeDragBridge(_node,Log);
        _clipboard=new ClipboardSync(_node,()=>_state.Settings.ClipboardEnabled,Log,Dispatcher,()=>_state.Settings.ClipboardHistoryEnabled);
        _historyStore=new HistoryStore(Path.Combine(_state.DirectoryPath,"ClipboardHistory"),new HistoryRetention(_state.Settings.ClipboardHistoryMaxItems,_state.Settings.ClipboardHistoryDays,_state.Settings.ClipboardHistoryMaxBytes));
        _historyBridge=new HistoryBridge(_clipboard,_historyStore,_node,Log,kind=>_state.Settings.ClipboardHistoryEnabled&&(kind switch {"image"=>_state.Settings.ClipboardHistoryCaptureImages,"files"=>_state.Settings.ClipboardHistoryCaptureFiles,_=>true}));
        ClipboardSettings=new ClipboardSettingsModel(_state.Settings,_state.Save,_historyStore,_clipboard,SetHistoryHotkey,_lifetime.Token);
        _node.Log+=Log;
        _node.PeersChanged+=()=>UI(RefreshPeers);
        _node.MessageReceived+=ReceiveControlMessage;
        _node.TransferProgress+=(peer,progress)=>UI(()=>{TrackUpdateTransfer(peer,progress);OnProgress(progress);});
        _router.EdgeCrossing+=(from,to,x,y,left)=>UI(()=>CrossEdge(from,to,x,y,left));
        _router.ActiveChanged+=id=>UI(RefreshStatus);
        _router.RemoteLeftChanged+=down=>
        {
            _drag.UpdateRemoteButton(down);
            if(!down)UI(()=>{if(_sourceCapture.Peer!=Guid.Empty)AbortSourceCapture();});
        };
        _router.PhysicalLeftChanged+=down=>
        {
            if(_router.ActiveId==_node.LocalId)_drag.UpdateRemoteButton(down);
            if(!down)UI(()=>{if(!_crossCommitted)_crossStop?.Cancel();_logicalPaths=null;_logicalOffer=Guid.Empty;_logicalSource=Guid.Empty;});
        };
        _router.CancelDragRequested+=()=>
        {
            _drag.CancelRemoteDrag();
            var cancelledCrossing=_crossStop;
            var cancelledIncoming=_incoming;
            var cancelledSource=_sourceCapture;
            UI(()=>
            {
                if(ReferenceEquals(_crossStop,cancelledCrossing))_crossStop?.Cancel();
                if(ReferenceEquals(_incoming,cancelledIncoming))_incoming?.Cancel("控制权退出或按下取消键");
                if(_sourceCapture==cancelledSource&&_sourceCapture.Peer!=Guid.Empty)AbortSourceCapture();
            });
        };
        _router.EmergencyReturned+=()=>UI(()=>{CancelDrag();Log("已通过紧急快捷键返回本机。");});
        Resources["GridBrush"]=MakeGrid();
        SideHost.Text=ConnHost.Text=_state.Settings.DeviceName;
        SideIp.Text=LocalAddresses();
        LocalPairingAddress.Text=SideIp.Text;
        FootLeft.Text=$"{_state.Settings.DeviceName} · 端口 {_listeningPort}";
        StartWithWindowsCheck.IsChecked=_state.Settings.StartWithWindows;
        DarkCheck.IsChecked=_state.Settings.DarkTheme;
        ApplyTheme(_state.Settings.DarkTheme);
        _tray=new Forms.NotifyIcon{Text="NekoBridge · 局域网键鼠共享",Icon=new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/nekobridge.ico")).Stream),Visible=true};
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("显示 NekoBridge",null,(_,_)=>UI(ShowWindow));
        menu.Items.Add("剪贴板历史",null,(_,_)=>UI(ShowClipboardHistory));
        menu.Items.Add("暂停 / 恢复共享",null,(_,_)=>UI(TogglePause));
        menu.Items.Add("回到本机",null,(_,_)=>UI(()=>{_router.ReturnLocal();CancelDrag();}));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 NekoBridge",null,(_,_)=>UI(Quit));
        _tray.ContextMenuStrip=menu;_tray.DoubleClick+=(_,_)=>UI(ShowWindow);
        _pairingTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        _pairingTimer.Tick+=(_,_)=>RefreshStatus();
        _pairingTimer.Start();
        Loaded+=(_,_)=>StartNode();
        Closing+=OnClosing;
        _uiReady=true;InitializeClipboardSettings();RefreshPeers();
        _=CheckUpdatesAsync();
        Loaded+=(_,_)=>{NavConn.IsChecked=true;Nav_Checked(NavConn,new RoutedEventArgs());Layout();};
    }
    internal void InitializeInBackground()=>StartNode();
    private async void StartNode()
    {
        if(_initialized)return;_initialized=true;
        try
        {
            _ = new WindowInteropHelper(this).EnsureHandle();
            await _router.Ready;
            await _node.StartAsync(_lifetime.Token);
            _hwnd=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwnd?.AddHook(WindowMessages);
            if(_hwnd!=null)
            {
                _historyHotkey=new HistoryHotkey(_hwnd,ShowClipboardHistory);
                try{_historyHotkey.Set(_state.Settings.ClipboardHistoryHotkey);}
                catch(Exception ex){Log($"历史快捷键注册失败：{ex.Message}；可从托盘打开并修改。");}
            }
            if(!AddClipboardFormatListener(new WindowInteropHelper(this).Handle))Log("无法监听剪贴板，其他共享功能不受影响。");
            _started=true;RefreshStatus();
            Log($"已监听局域网端口 {_listeningPort}，设备身份已固定。首次连接需开启配对。");
            foreach(var peer in _state.Settings.Peers.ToArray())
            {
                try{await _node.ConnectAsync($"{peer.Host}:{peer.Port}",null,_lifetime.Token);}
                catch(Exception ex){Log($"重连 {peer.Name} 失败：{ex.Message}");}
            }
        }
        catch(Exception ex){_startupError=ex.Message;_router.SetPaused(true);PauseToggle.IsChecked=true;RefreshStatus();Log(ex.Message);ShowNotice("启动失败",ex.Message,false);}
    }
    private void UI(Action action)
    {
        if(Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(action,DispatcherPriority.Normal);
    }
    private void Log(string message)=>UI(()=>
    {
        _logs.Insert(0,$"{DateTime.Now:HH:mm:ss}  {message}");
        while(_logs.Count>200)_logs.RemoveAt(_logs.Count-1);
        bool failed=message.Contains("失败")||message.Contains("拒绝");
        if(failed||message.Contains("取消"))
        {
            TransferText.Text=message;TransferText.ToolTip=message;
            long now=Environment.TickCount64;
            if(failed&&(_lastWarningAt is null||now-_lastWarningAt.Value>=30000))
            {
                _lastWarningAt=now;
                _tray.ShowBalloonTip(5000,"NekoBridge",message,Forms.ToolTipIcon.Warning);
            }
        }
    });
    private string NameOf(Guid id)=>id==_node.LocalId?_state.Settings.DeviceName:_node.Peers.FirstOrDefault(p=>p.Id==id)?.DisplayName??"未知设备";
    private string LocalAddresses()
    {
        string[] addresses= Dns.GetHostAddresses(Dns.GetHostName()).Where(a=>a.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a)).Select(a=>$"{a}:{_listeningPort}").ToArray();
        return addresses.Length==0?$"未找到局域网 IPv4 地址 · 端口 {_listeningPort}":string.Join("\n",addresses);
    }
    private void RefreshPeers()
    {
        if(_screenDrag!=Guid.Empty)FinishScreenDrag(false);
        var peers=_node.Peers.ToArray();RefreshPeerCards(peers);
        var saved=SyncOnPeers(peers);
        _router.SetLayout(saved);
        _screens.Clear();
        foreach(var cell in saved)
        {
            var peer=peers.FirstOrDefault(p=>p.Id==cell.Id);
            if(cell.Id!=_node.LocalId&&peer==null)continue;
            bool local=cell.Id==_node.LocalId;
            int width=local?_router.Width:Math.Max(1,peer!.Width),height=local?_router.Height:Math.Max(1,peer!.Height);
            _screens.Add(new ScreenItem{Id=cell.Id,Name=NameOf(cell.Id),Primary=local,Online=local||peer!.IsConnected,
                Column=cell.Column,Row=cell.Row,PixelWidth=width,PixelHeight=height,Res=$"{width} × {height}"});
        }
        Screens.ItemsSource=_screens;Layout();
        ScreenCount.Text=$"{_screens.Count} 台设备";
        RefreshStatus();
    }
    private void RefreshStatus()
    {
        if(!_uiReady)return;
        int online=_node.Peers.Count(p=>p.IsConnected);
        bool paused=_router.Paused;
        StateText.Text=_startupError!=null?"启动失败":!_started?"正在启动":paused?"共享已暂停":online>0?"已连接":"仅本机";
        StateSub.Text=$"{online} 台在线 · 控制 {NameOf(_router.OwnerId!=Guid.Empty?_router.OwnerId:_router.ActiveId)}";
        ShareText.Text=!_started?"共享未启动":paused?"已暂停":"共享已就绪";
        ShareText.Foreground=(Brush)FindResource(paused?"Vein":"Muted");
        StateDot.Stroke=(Brush)FindResource(_startupError!=null?"Vein":online>0?"LimeDeep":"Muted");
        bool pairing=_node.PairingEnabled;
        PairingCodeText.Text=pairing?_node.PairingKey:"未开启";
        CardAction.Content=pairing?"关闭配对":"开启配对 →";
        CardAction.IsEnabled=_started;
        RefreshConnectionFlow();
    }
    private async void Connect_Click(object sender,RoutedEventArgs e)
    {
        if(_connecting||!_started)return;
        using var attempt=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _guideConnectStop=attempt;_connecting=true;_connectionSucceeded=false;
        ConnectionFeedback.Text="正在连接…";RefreshConnectionFlow();
        try
        {
            await _node.ConnectAsync(JoinAddress.Text.Trim(),JoinCode.Password,attempt.Token);
            if(!ReferenceEquals(_guideConnectStop,attempt))return;
            JoinCode.Clear();_connectionSucceeded=true;ConnectionFeedback.Text="连接成功。";RefreshPeers();
        }
        catch(OperationCanceledException)when(attempt.IsCancellationRequested) { }
        catch(Exception ex)
        {
            if(ReferenceEquals(_guideConnectStop,attempt)){ConnectionFeedback.Text=$"连接失败：{ex.Message}";Log(ConnectionFeedback.Text);}
        }
        finally
        {
            if(ReferenceEquals(_guideConnectStop,attempt)){_guideConnectStop=null;_connecting=false;if(!_quitting)RefreshConnectionFlow();}
        }
    }
    private void Pairing_Click(object sender,RoutedEventArgs e)
    {
        _node.PairingEnabled=!_node.PairingEnabled;
        _pairingStarted=_node.PairingEnabled?DateTime.UtcNow:null;
        _guideOpenedPairing=_node.PairingEnabled;
        PairingCodeText.Text=_node.PairingEnabled?_node.PairingKey:"未开启";
        CardAction.Content=_node.PairingEnabled?"关闭配对":"开启配对";
        RefreshStatus();
        Log(_node.PairingEnabled?"已临时开启配对，请只向可信电脑提供密钥。":"已关闭配对。");
    }
    private async void Discover_Click(object sender,RoutedEventArgs e)
    {
        NearbyButton.IsEnabled=false;
        try
        {
            var nearby=await _node.DiscoverAsync(_lifetime.Token);NearbyList.ItemsSource=nearby;
            ConnectionFeedback.Text=nearby.Count==0?"未找到附近设备，可手动填写地址。":"选择对方设备，再填写安全密钥。";
        }
        catch(Exception ex){ConnectionFeedback.Text=$"查找失败：{ex.Message}";Log(ConnectionFeedback.Text);}
        finally{NearbyButton.IsEnabled=true;}
    }
    private void Nearby_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(NearbyList.SelectedItem is DiscoveredPeer p)JoinAddress.Text=$"{p.Host}:{p.Port}";
    }
    private void Pause_Click(object sender,RoutedEventArgs e)=>TogglePause();
    private void TogglePause(){PauseToggle.IsChecked=!(PauseToggle.IsChecked==true);}
    private void ReturnLocal_Click(object sender,RoutedEventArgs e){_router.ReturnLocal();CancelDrag();}
    private void RemovePeer_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement{DataContext:PeerCard peer})return;
        if(_peerRequests.Contains(peer.Id))return;
        if(!ShowNotice("移除设备",$"移除 {peer.Name} 的信任与连接？再次使用需要重新配对。",true))return;
        _router.ReturnLocal();CancelDrag();_node.Disconnect(peer.Id,true);_peerCards.Remove(peer.Id);RefreshPeers();
    }
    private void ReceiveControlMessage(Guid peer,ControlMessage message)
    {
        if(message.Kind is "enter" or "leave" or "move" or "key" or "button" or "wheel" or "keepalive" or "yield")
        {
            if(_quitting)return;
            try{_router.Accept(peer,message);}
            catch(Exception ex){DragDiagnostics.Failure("input-"+message.Kind,ex);Log($"远端输入失败 [{message.Kind}, 0x{ex.HResult:X8}]：{ex.Message}");}
        }
        else UI(()=>HandleMessage(peer,message));
    }
    private async void HandleMessage(Guid peer,ControlMessage m)
    {
        try
        {
            switch(m.Kind)
            {
                case "layout2":ReceiveLayout(peer,m);break;
                case "layout":Log("排列同步需要两端都更新到 1.01。");break;
                case "capture2":await CaptureRemoteSource(peer,m);break;
                case "captured2":case "dragready":case "started2":case "released2":
                    if(_dragReplies.TryGetValue((peer,m.Id,m.Kind),out var reply))reply.TrySetResult(m);
                    break;
                case "prepare2":await PrepareTarget(peer,m);break;
                case "commit2":
                    if(_incoming is { } incoming&&incoming.Peer==peer&&incoming.Id==m.Id&&_router.OwnerId==peer)
                        CommitTarget(incoming);
                    break;
                case "release2":
                    if(_sourceCapture.Peer==peer&&_sourceCapture.Session==m.Id&&_router.OwnerId==peer)
                    {
                        try
                        {
                            if(_sourceCapture.Offer==Guid.Empty||!NativeDragBridge.SystemLeftDown)
                                throw new IOException("源端左键已松开，无法交接。");
                            _router.ReleaseSourceDrag();_sourceCapture=default;
                            SendObserved(peer,new ControlMessage{Kind="released2",Id=m.Id});
                        }
                        catch(Exception ex)
                        {
                            Log($"源端交接失败：{ex.Message}");
                            AbortSourceCapture();
                            SendObserved(peer,new ControlMessage{Kind="released2",Id=m.Id,Flags=1,Text="源端未能交接拖放，请检查源端提示。"});
                        }
                    }
                    break;
                case "abort2":
                    if(_incoming is { } aborted&&aborted.Peer==peer&&aborted.Id==m.Id)aborted.Cancel("主控端取消交接");
                    if(_sourceCapture.Peer==peer&&_sourceCapture.Session==m.Id)
                    AbortSourceCapture();
                    break;
                case "capture":case "dragoffer":case "dragcancel":
                    Log("跨屏拖放已拒绝：请更新两端版本。");break;
                case "manualoffer":
                    await _node.SendAsync(peer,new ControlMessage{Kind="manualdone",Id=m.Id},_lifetime.Token);
                    Log("已拒绝旧版手动发送请求；请使用桌面或资源管理器跨屏拖放。");break;
                case "manualdone":_node.RevokeOffer(m.Id);break;
                case "clipboard":
                    _clipboard.Receive(peer,m);
                    break;
                default:_router.Accept(peer,m);break;
            }
        }
        catch(OperationCanceledException){Log("操作已取消。");}
        catch(Exception ex){DragDiagnostics.Failure("remote-"+m.Kind,ex);Log($"远端操作失败 [{m.Kind}, 0x{ex.HResult:X8}]：{ex.Message}");}
    }
    private async Task<ControlMessage> RequestDrag(Guid peer,Guid id,string kind,Action send,CancellationToken ct)
    {
        var key=(peer,id,kind);
        var reply=new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dragReplies.Add(key,reply);
        try
        {
            send();
            DragDiagnostics.Stage(id,"request-"+kind);
            var response=await reply.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
            DragDiagnostics.Stage(id,"reply-"+kind+"-"+response.Flags);
            if(response.Flags!=0)throw new IOException(response.Text??"对端拒绝拖放。");
            return response;
        }
        catch(TimeoutException){throw new IOException("对端未确认拖放，请检查连接并更新两端版本。");}
        finally{_dragReplies.Remove(key);}
    }
    private async Task<IReadOnlyList<string>> CaptureSource(CancellationToken ct)
    {
        if(!NativeDragBridge.SystemLeftDown||!NativeMethods.GetCursorPos(out var point))throw new IOException("源端未检测到按住的左键。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try{return await _drag.CaptureAtEdgeAsync(point.X,point.Y,timeout.Token);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new IOException("未捕获到文件，请从桌面或资源管理器重新拖动。");}
    }
    private async void CrossEdge(Guid from,Guid to,int x,int y,bool left)
    {
        if(_busyCrossing){_router.CancelCrossing();return;}
        _busyCrossing=true;
        long generation=_router.Generation;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _crossStop=stop;
        var ct=stop.Token;
        Guid id=Guid.NewGuid(),capture=Guid.Empty;
        FileOffer? offer=null;
        IncomingDrag? local=null;
        bool preparing=false,switched=false,success=false;
        try
        {
            if(!left){if(!_router.CompleteCrossing(to,x,y,false,generation))_router.CancelCrossing();return;}
            Held();
            var source=_logicalSource;
            var paths=_logicalPaths;
            var sourceOffer=_logicalOffer;
            bool fresh=source==Guid.Empty;
            if(fresh)
            {
                source=from;
                if(from==_node.LocalId)paths=await CaptureSource(ct);
                else
                {
                    capture=Guid.NewGuid();
                    var response=await RequestDrag(from,capture,"captured2",()=>_router.SendOrdered(from,new ControlMessage{Kind="capture2",Id=capture}),ct);
                    if(!Guid.TryParse(response.Payload,out sourceOffer)||sourceOffer==Guid.Empty)throw new IOException("源端未返回有效清单。");
                }
            }
            Held();
            if(to==source)
            {
                if(!_router.CompleteCrossing(to,x,y,true,generation,beforeDragSwitch:()=>{if(from==_node.LocalId)_drag.CancelRemoteDrag();}))throw new OperationCanceledException();
                success=true;CancelDrag();Log("已回到文件来源电脑，本次跨机拖放已取消。");return;
            }
            if(to==_node.LocalId)
            {
                local=StartTarget(_node.LocalId,id,source,sourceOffer);
                await local.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
            }
            else
            {
                offer=paths!=null?await Task.Run(()=>_node.CreateOffer(to,paths),ct):_node.CreateRelayOffer(to,source,await _node.GetOfferAsync(source,sourceOffer,ct));
                Held();preparing=true;
                await RequestDrag(to,id,"dragready",()=>_router.SendOrdered(to,new ControlMessage{Kind="prepare2",Id=id,Payload=offer.Id.ToString()}),ct);
            }
            Held();
            if(capture!=Guid.Empty)
            {
                await RequestDrag(from,capture,"released2",()=>_router.SendOrdered(from,new ControlMessage{Kind="release2",Id=capture}),ct);
                Held();
            }
            void BeforeSwitch()
            {
                if(fresh&&from==_node.LocalId)
                {
                    if(!NativeDragBridge.SystemLeftDown)throw new OperationCanceledException();
                    _router.ReleaseSourceDrag();
                }
                else if(from==_node.LocalId)_drag.CancelRemoteDrag();
            }
            void Switch(ControlMessage? commit)
            {
                if(!_router.CompleteCrossing(to,x,y,true,generation,commit,beforeDragSwitch:BeforeSwitch))throw new OperationCanceledException();
                switched=true;_crossCommitted=true;
            }
            if(local!=null)
            {
                Switch(null);CommitTarget(local);
                await local.Started.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
            }
            else await RequestDrag(to,id,"started2",()=>Switch(new ControlMessage{Kind="commit2",Id=id}),ct);
            _router.DragStarted();
            _logicalSource=source;_logicalPaths=paths;_logicalOffer=sourceOffer;
            _currentDragTarget=to;_targetSession=id;success=true;
            if(!_router.LeftDown){_logicalPaths=null;_logicalOffer=Guid.Empty;_logicalSource=Guid.Empty;}
        }
        catch(OperationCanceledException){if(!_quitting)Log("跨屏拖放已取消：按钮已松开或控制会话已改变。");}
        catch(Exception ex){DragDiagnostics.Failure("cross-edge",ex);Log($"跨屏拖放失败 [0x{ex.HResult:X8}]：{ex.Message}");}
        finally
        {
            if(left&&!success)
            {
                if(_router.Generation==generation)_router.CancelCrossing();
                if(local!=null&&ReferenceEquals(_incoming,local))local.Cancel("主控端交接未完成");
                if(preparing)_router.SendOrdered(to,new ControlMessage{Kind="abort2",Id=id});
                if(offer!=null)_node.RevokeOffer(offer.Id);
                if(capture!=Guid.Empty)_router.SendOrdered(from,new ControlMessage{Kind="abort2",Id=capture});
                if(switched&&_router.ActiveId==to){_router.ReturnLocal();CancelDrag();}
            }
            _router.DragStarted();
            _crossStop=null;_crossCommitted=false;_busyCrossing=false;
        }
        void Held()
        {
            ct.ThrowIfCancellationRequested();
            if(!_router.LeftDown||_router.Generation!=generation)throw new OperationCanceledException();
        }
    }
    private async Task CaptureRemoteSource(Guid peer,ControlMessage request)
    {
        if(_router.OwnerId!=peer||request.Id==Guid.Empty||_sourceCapture!=default||_sourceCaptureStop!=null)return;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        stop.CancelAfter(TimeSpan.FromSeconds(12));
        _sourceCaptureStop=stop;
        _sourceCapture=(peer,request.Id,Guid.Empty);
        FileOffer? offer=null;
        try
        {
            var paths=await CaptureSource(stop.Token);
            offer=await Task.Run(()=>_node.CreateOffer(peer,paths),stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if(_sourceCapture!=(peer,request.Id,Guid.Empty)||_router.OwnerId!=peer||!NativeDragBridge.SystemLeftDown)throw new OperationCanceledException();
            _sourceCapture=(peer,request.Id,offer.Id);
            await _node.SendAsync(peer,new ControlMessage{Kind="captured2",Id=request.Id,Payload=offer.Id.ToString()},_lifetime.Token);
        }
        catch(Exception ex)
        {
            if(offer!=null)_node.RevokeOffer(offer.Id);
            if(_sourceCapture.Session==request.Id)_sourceCapture=default;
            DragDiagnostics.Failure("source-capture",ex);
            Log($"源端拖放失败 [0x{ex.HResult:X8}]：{ex.Message}");
            SendObserved(peer,new ControlMessage{Kind="captured2",Id=request.Id,Flags=1,Text="源端未能准备文件，请检查源端提示。"});
        }
        finally{_sourceCaptureStop=null;}
    }
    private void AbortSourceCapture()
    {
        _sourceCaptureStop?.Cancel();
        if(_sourceCapture.Offer!=Guid.Empty)_node.RevokeOffer(_sourceCapture.Offer);
        _sourceCapture=default;
    }
    private IncomingDrag StartTarget(Guid peer,Guid id,Guid source,Guid offer)
    {
        if(_incoming!=null)throw new IOException("目标已有拖放会话。");
        var incoming=new IncomingDrag(peer,id,_lifetime.Token);
        _incoming=incoming;
        incoming.Stop.CancelAfter(TimeSpan.FromSeconds(12));
        RunTarget(incoming,source,offer);
        return incoming;
    }
    private async void RunTarget(IncomingDrag incoming,Guid source,Guid offer)
    {
        try
        {
            var operation=_drag.BeginRemoteDragAsync(source,offer,incoming.Ready,incoming.Commit.Task,incoming.Started,incoming.Stop.Token);
            try
            {
                await incoming.Started.Task;
                incoming.Stop.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            finally{await operation;}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){DragDiagnostics.Failure("native-target",ex);Log($"原生拖放失败 [0x{ex.HResult:X8}]：{ex.Message}");}
        finally
        {
            incoming.Ready.TrySetCanceled();incoming.Started.TrySetCanceled();
            if(ReferenceEquals(_incoming,incoming))_incoming=null;
            incoming.Close();
        }
    }
    private void CommitTarget(IncomingDrag incoming)
    {
        if(!incoming.Ready.Task.IsCompletedSuccessfully||incoming.Stop.IsCancellationRequested||incoming.Commit.Task.IsCompleted)return;
        DragDiagnostics.Stage(incoming.Id,"target-commit");
        incoming.Commit.TrySetResult();
    }
    private async Task PrepareTarget(Guid peer,ControlMessage m)
    {
        IncomingDrag? incoming=null;
        bool ready=false;
        try
        {
            if(_router.Paused||(_router.OwnerId!=Guid.Empty&&_router.OwnerId!=peer)||_router.ActiveId!=_node.LocalId||m.Id==Guid.Empty||!Guid.TryParse(m.Payload,out var offer)||offer==Guid.Empty)
                throw new IOException("目标端不可接管拖放或请求无效。");
            DragDiagnostics.Stage(m.Id,"target-prepare");
            incoming=StartTarget(peer,m.Id,peer,offer);
            await incoming.Ready.Task;
            DragDiagnostics.Stage(m.Id,"target-ready");
            await _node.SendAsync(peer,new ControlMessage{Kind="dragready",Id=m.Id},_lifetime.Token);
            ready=true;
            await incoming.Started.Task;
            await _node.SendAsync(peer,new ControlMessage{Kind="started2",Id=m.Id},_lifetime.Token);
        }
        catch(OperationCanceledException) when(_quitting||incoming?.CancelReason!=null)
        {
            Log("目标端拖放已取消："+(incoming?.CancelReason??"程序退出"));
        }
        catch(Exception ex)
        {
            if(incoming!=null&&ReferenceEquals(_incoming,incoming))incoming.Cancel("目标端启动失败");
            var failure=ex is OperationCanceledException
                ?new OperationCanceledException("目标拖放在启动确认前已结束；请保持左键按住到跨屏完成。",ex):ex;
            DragDiagnostics.Failure("target-prepare",failure);
            Log($"目标端拖放失败 [0x{failure.HResult:X8}]：{failure.Message}");
            SendObserved(peer,new ControlMessage{Kind=ready?"started2":"dragready",Id=m.Id,Flags=1,Text="目标未能准备或启动原生拖放，请检查目标端提示。"});
        }
    }
    private void CancelDrag()
    {
        _router.CancelCrossing();_crossStop?.Cancel();_incoming?.Cancel("本机取消操作");_drag.CancelRemoteDrag();
        AbortSourceCapture();
        _logicalPaths=null;_logicalOffer=Guid.Empty;_logicalSource=Guid.Empty;
        if(_currentDragTarget!=Guid.Empty&&_currentDragTarget!=_node.LocalId)
            _router.SendOrdered(_currentDragTarget,new ControlMessage{Kind="abort2",Id=_targetSession});
        _currentDragTarget=Guid.Empty;_targetSession=Guid.Empty;
    }
    private async void SendObserved(Guid peer,ControlMessage message)
    {
        try{await _node.SendAsync(peer,message,_lifetime.Token);}catch(Exception ex){if(!_quitting)Log($"发送失败：{ex.Message}");}
    }
    private void OnProgress(FileTransferProgress p)
    {
        string line=$"{p.State} · {p.Name} · {FormatBytes(p.Bytes)} / {FormatBytes(p.TotalBytes)}";
        TransferText.Text=line;TransferText.ToolTip=line;
        TransferBar.Width=ActualWidth*(p.TotalBytes>0?Math.Clamp(p.Bytes/(double)p.TotalBytes,0,1):0);
    }
    private static string FormatBytes(long bytes)=>bytes>=1073741824?$"{bytes/1073741824d:F2} GB":bytes>=1048576?$"{bytes/1048576d:F1} MB":bytes>=1024?$"{bytes/1024d:F1} KB":$"{bytes} B";
    private void Startup_Changed(object sender,RoutedEventArgs e)
    {
        if(!_uiReady)return;
        bool previous=_state.Settings.StartWithWindows;
        try
        {
            bool startup=StartWithWindowsCheck.IsChecked==true;
            using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(startup)key.SetValue("NekoBridge",$"\"{Environment.ProcessPath}\" --background");else key.DeleteValue("NekoBridge",false);
            _state.Settings.StartWithWindows=startup;_state.Save();
        }
        catch(Exception ex){_state.Settings.StartWithWindows=previous;_uiReady=false;StartWithWindowsCheck.IsChecked=previous;_uiReady=true;ShowNotice("开机启动设置失败",ex.Message,false);}
    }
    private nint WindowMessages(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x31D)_clipboard.HandleClipboardUpdate();
        if(message==0x1A)UI(UpdateDeviceWallpaper);
        return 0;
    }
    private bool ShowNotice(string title,string text,bool confirm)
    {
        var buttons=confirm?MessageBoxButton.OKCancel:MessageBoxButton.OK;
        var icon=confirm?MessageBoxImage.Question:MessageBoxImage.Information;
        return (IsVisible?MessageBox.Show(this,text,title,buttons,icon):MessageBox.Show(text,title,buttons,icon))==MessageBoxResult.OK;
    }
    private void ShowWindow(){Show();WindowState=WindowState.Normal;Activate();}
    private void OnClosing(object? sender,CancelEventArgs e){if(!_quitting){e.Cancel=true;Hide();}}
    private async void Quit()
    {
        if(_quitting)return;_quitting=true;
        _pairingTimer.Stop();_router.ReturnLocal();CancelDrag();
        _lifetime.Cancel();
        _historyHotkey?.Dispose();
        _historyWindow?.Hide();
        await _historyBridge.StopAsync();
        _historyWindow?.Close();
        _historyStore.Dispose();
        _clipboard.Dispose();_router.Dispose();_drag.Dispose();
        if(_hwnd!=null){RemoveClipboardFormatListener(_hwnd.Handle);_hwnd.RemoveHook(WindowMessages);}
        _tray.Visible=false;_tray.Dispose();
        try{await _node.DisposeAsync();}catch(Exception ex){App.LogFailure(ex);}
        _state.Identity.Dispose();_lifetime.Dispose();Close();Application.Current.Shutdown();
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint hwnd);
}
