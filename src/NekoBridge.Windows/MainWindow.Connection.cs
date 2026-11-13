using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NekoBridge.Core;

namespace NekoBridge.Windows;

public partial class MainWindow
{
    private static readonly Brush ConnectionActiveBrush=new SolidColorBrush(Color.FromRgb(27,33,48));
    private bool? _connectionController;
    private bool _connectionPrepared;
    private bool _connectionSucceeded;
    private bool _connecting;
    private DateTime? _pairingStarted;
    private System.Threading.CancellationTokenSource? _guideConnectStop;
    private bool _connectionTransition;
    private FrameworkElement? _connectionView;
    private int _connectionStage;
    private bool _connectionOverviewRequested;
    private readonly System.Collections.Generic.HashSet<Guid> _connectionExistingPeers=new();

    private void ConnectionScroll_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        // Subtract the content margin: binding to ViewportHeight without this
        // subtraction makes the ScrollViewer overflow even on an empty page.
        if(ConnectionCanvas==null)return;
        ConnectionCanvas.MinHeight=Math.Max(0,ConnectionScroll.ActualHeight-12);
    }

    private static void AnimateConnectionEntry(FrameworkElement element)
    {
        if(!SystemParameters.ClientAreaAnimation)return;
        var duration=new Duration(TimeSpan.FromMilliseconds(300));
        element.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,duration){FillBehavior=FillBehavior.Stop});
        var transform=new TranslateTransform();element.RenderTransform=transform;
        transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(18,0,duration){FillBehavior=FillBehavior.Stop,EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
    private void ShowConnectionView(FrameworkElement view)
    {
        if(_connectionView==view)return;
        if(_connectionTransition)return;
        var previous=_connectionView;
        _connectionView=view;
        void Complete()
        {
            foreach(var element in new FrameworkElement[]{ConnectionIntro,ConnectionSteps,ConnectionOverview})
                element.Visibility=element==view?Visibility.Visible:Visibility.Collapsed;
            if(previous!=null)previous.BeginAnimation(OpacityProperty,null);
            _connectionTransition=false;
            AnimateConnectionEntry(view);
            RefreshConnectionFlow();
        }
        if(previous!=null&&SystemParameters.ClientAreaAnimation)
        {
            _connectionTransition=true;
            var fade=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(150)){FillBehavior=FillBehavior.Stop};
            fade.Completed+=(_,_)=>Complete();
            previous.BeginAnimation(OpacityProperty,fade);
        }
        else Complete();
    }
    private void ControllerRole_Click(object sender,RoutedEventArgs e)=>SelectConnectionRole(true);
    private void ReceiverRole_Click(object sender,RoutedEventArgs e)=>SelectConnectionRole(false);
    private void SelectConnectionRole(bool controller)
    {
        if(_connecting||_connectionTransition)return;
        if(controller)CancelGuidePairing();
        _connectionController=controller;_connectionPrepared=false;_connectionSucceeded=false;
        _connectionExistingPeers.Clear();
        _connectionExistingPeers.UnionWith(_node.Peers.Where(p=>p.IsConnected).Select(p=>p.Id));
        _connectionOverviewRequested=false;_connectionStage=0;
        JoinCode.Clear();ConnectionFeedback.Text="";
        ShowConnectionView(ConnectionSteps);RefreshStatus();
    }
    private void ChangeConnectionRole_Click(object sender,RoutedEventArgs e)
    {
        if(_connecting||_connectionTransition)return;
        CancelGuidePairing();
        _connectionController=null;_connectionOverviewRequested=false;
        JoinCode.Clear();ShowConnectionView(ConnectionIntro);RefreshStatus();
    }
    private void AddConnection_Click(object sender,RoutedEventArgs e)=>ChangeConnectionRole_Click(sender,e);
    private void FinishConnection_Click(object sender,RoutedEventArgs e)
    {
        if(_connectionTransition)return;
        _connectionController=null;_connectionOverviewRequested=true;
        CancelGuidePairing();
        ShowConnectionView(ConnectionOverview);RefreshStatus();
    }
    private void Prepared_Click(object sender,RoutedEventArgs e)
    {
        _connectionPrepared=true;RefreshConnectionFlow();JoinAddress.Focus();
    }
    private void ConnectionPrevious_Click(object sender,RoutedEventArgs e)
    {
        if(_connecting)return;
        _connectionPrepared=false;_connectionSucceeded=false;RefreshConnectionFlow();
    }
    private void RefreshConnectionFlow()
    {
        var peers=_node.Peers.ToArray();var online=peers.Where(p=>p.IsConnected).ToArray();
        PairedDevicesExpander.Header=$"已配对设备（{peers.Length}）";
        OverviewTitle.Text=online.Length>0?"设备已连接。":"已配对设备。";
        ReturnDevicesButton.Visibility=peers.Length>0&&_connectionView!=ConnectionOverview?Visibility.Visible:Visibility.Collapsed;
        ReturnDevicesButton.IsEnabled=!_connectionTransition;
        ControllerRoleButton.IsEnabled=ReceiverRoleButton.IsEnabled=ChangeRoleButton.IsEnabled=!_connecting&&!_connectionTransition;
        if(_connectionController is not bool controller)
        {
            if(_connectionView==null)ShowConnectionView(peers.Length>0?ConnectionOverview:ConnectionIntro);
            else if(!_connectionTransition&&_connectionView!=ConnectionIntro&&peers.Length==0&&!_connectionOverviewRequested)ShowConnectionView(ConnectionIntro);
            return;
        }
        if(online.Length==0)_connectionSucceeded=false;
        ConnectionRoleHint.Text=controller?"CONTROLLER":"RECEIVER";
        ConnectionFlowTitle.Text=controller?"控制端":"被控端";
        ConnectionLabelOne.Text=controller?"准备对方主机":"开启配对";
        ConnectionLabelTwo.Text=controller?"填写连接信息":"提供连接信息";
        PrepareTitle.Text=controller?"先在另一台主机开启配对":"开启配对";
        PrepareBody.Text=controller?"打开对方的 NekoBridge，选择“被控端”，点击“开启配对”。":"开启后会显示本机地址和安全密钥。配对开放五分钟。";
        CardAction.Visibility=controller?Visibility.Collapsed:Visibility.Visible;
        PreparedButton.Visibility=controller?Visibility.Visible:Visibility.Collapsed;
        PreparedButton.IsEnabled=_started&&!_connecting;
        ConnectStepTitle.Text=controller?"连接对方主机":"把连接信息填到控制端";
        ControllerDetails.Visibility=controller?Visibility.Visible:Visibility.Collapsed;
        ReceiverDetails.Visibility=controller?Visibility.Collapsed:Visibility.Visible;
        bool connected=controller?online.Length>0&&_connectionSucceeded:online.Any(p=>!_connectionExistingPeers.Contains(p.Id));
        int stage=connected?3:(controller?_connectionPrepared:_node.PairingEnabled)?2:1;
        ConnectionStepOne.Visibility=stage==1?Visibility.Visible:Visibility.Collapsed;
        ConnectionStepTwo.Visibility=stage==2?Visibility.Visible:Visibility.Collapsed;
        ConnectionStepThree.Visibility=stage==3?Visibility.Visible:Visibility.Collapsed;
        if(stage!=_connectionStage)
        {
            _connectionStage=stage;
            AnimateConnectionEntry(stage==1?ConnectionStepOne:stage==2?ConnectionStepTwo:ConnectionStepThree);
        }
        ConnectionStepCount.Text=$"步骤 {stage:00} / 03";
        var balls=new[]{ConnectionBallOne,ConnectionBallTwo,ConnectionBallThree};
        var numbers=new[]{ConnectionNumberOne,ConnectionNumberTwo,ConnectionNumberThree};
        var labels=new[]{ConnectionLabelOne,ConnectionLabelTwo,ConnectionLabelThree};
        for(int i=0;i<3;i++)
        {
            bool active=i+1==stage,done=i+1<stage;
            balls[i].Background=active?ConnectionActiveBrush:(Brush)FindResource(done?"LimeWash":"Paper");
            balls[i].BorderBrush=(Brush)FindResource(done?"LimeDeep":"Line");
            numbers[i].Text=done?"✓":$"{i+1:00}";
            numbers[i].Foreground=active?Brushes.White:(Brush)FindResource(done?"LimeDeep":"Muted");
            labels[i].Foreground=(Brush)FindResource(active?"Ink":done?"LimeDeep":"Muted");
            labels[i].FontWeight=active?FontWeights.SemiBold:FontWeights.Normal;
        }
        ConnectedDevicesText.Text=string.Join("、",online.Select(p=>p.DisplayName));
        if(_node.PairingEnabled)
        {
            var remaining=_pairingStarted is {} started?TimeSpan.FromMinutes(5)-(DateTime.UtcNow-started):TimeSpan.Zero;
            PairingWaitText.Text=remaining>TimeSpan.Zero?$"等待控制端连接 · 剩余 {remaining.Minutes:00}:{remaining.Seconds:00}":"等待控制端连接";
        }
        else PairingWaitText.Text="配对已关闭";
        ConnectButton.IsEnabled=_started&&!_connecting;
    }
    private void CopyPairingInfo_Click(object sender,RoutedEventArgs e)
    {
        if(!_node.PairingEnabled){ShowNotice("配对已关闭","请先开启配对。",false);return;}
        try{Clipboard.SetText($"{LocalPairingAddress.Text}\n{_node.PairingKey}");}
        catch(Exception ex){ShowNotice("复制失败",ex.Message,false);}
    }
}
