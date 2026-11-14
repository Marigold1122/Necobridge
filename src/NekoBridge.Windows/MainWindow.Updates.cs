using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using NekoBridge.Core;
using Velopack;
using Velopack.Sources;

namespace NekoBridge.Windows;
public partial class MainWindow
{
    private UpdateManager? _updateManager;
    private UpdateInfo? _availableUpdate;
    private bool _updateBusy;
    private readonly Dictionary<(Guid,Guid,string),int> _updateTransfers=new();
    private void TrackUpdateTransfer(Guid peer,FileTransferProgress progress)
    {
        string direction=progress.State.Contains("发送")?"send":"receive";
        var key=(peer,progress.TransferId,direction);
        if(progress.State is "正在发送" or "正在接收")_updateTransfers[key]=1;
        else _updateTransfers.Remove(key);
    }
    private void RefreshUpdateButtons()
    {
        UpdateCheckButton.IsEnabled=!_updateBusy;
        UpdateDownloadButton.Visibility=_availableUpdate!=null?Visibility.Visible:Visibility.Collapsed;
        UpdateDownloadButton.IsEnabled=!_updateBusy;
        UpdateRestartButton.Visibility=_updateManager?.UpdatePendingRestart!=null?Visibility.Visible:Visibility.Collapsed;
        UpdateRestartButton.IsEnabled=!_updateBusy;
    }
    private async Task CheckUpdatesAsync()
    {
        if(_updateBusy||_quitting)return;
        _updateBusy=true;UpdateStatusText.Text="正在检查更新…";RefreshUpdateButtons();
        try
        {
            UpdateVersionText.Text="当前版本 "+(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)??"未知");
            _updateManager??=new UpdateManager(new GithubSource("https://github.com/Marigold1122/Necobridge",null,false),new UpdateOptions{ExplicitChannel="win"});
            if(!_updateManager.IsInstalled)
            {
                UpdateStatusText.Text="此运行目录不支持在线更新，请使用新版便携包。";return;
            }
            if(_updateManager.UpdatePendingRestart!=null)
            {
                UpdateStatusText.Text="更新已准备好，等待你重启安装。";return;
            }
            _availableUpdate=await _updateManager.CheckForUpdatesAsync();
            if(_quitting)return;
            UpdateStatusText.Text=_availableUpdate==null?"当前已是最新版本。":"发现新版本 "+_availableUpdate.TargetFullRelease.Version;
            UpdateNotesText.Text=_availableUpdate?.TargetFullRelease.NotesMarkdown??"";
        }
        catch(Exception){if(!_quitting)UpdateStatusText.Text="暂时无法检查更新，请检查网络后重试。";}
        finally{_updateBusy=false;if(!_quitting)RefreshUpdateButtons();}
    }
    private async void UpdateCheck_Click(object sender,RoutedEventArgs e)=>await CheckUpdatesAsync();
    private async void UpdateDownload_Click(object sender,RoutedEventArgs e)
    {
        if(_updateBusy||_availableUpdate==null||_updateManager==null)return;
        _updateBusy=true;UpdateStatusText.Text="正在下载更新…";UpdateProgress.Visibility=Visibility.Visible;UpdateProgress.Value=0;RefreshUpdateButtons();
        try
        {
            await _updateManager.DownloadUpdatesAsync(_availableUpdate,value=>UI(()=>{if(!_quitting)UpdateProgress.Value=value;}),_lifetime.Token);
            if(!_quitting){UpdateStatusText.Text="更新已准备好，点击“重启并更新”安装。";_availableUpdate=null;}
        }
        catch(OperationCanceledException)when(_quitting) { }
        catch(Exception){if(!_quitting)UpdateStatusText.Text="更新下载失败，当前版本不受影响，可重新下载。";}
        finally{_updateBusy=false;if(!_quitting){UpdateProgress.Visibility=Visibility.Collapsed;RefreshUpdateButtons();}}
    }
    private void UpdateRestart_Click(object sender,RoutedEventArgs e)
    {
        if(_updateBusy||_updateManager?.UpdatePendingRestart is not {} pending)return;
        if(_busyCrossing||_incoming!=null||_sourceCapture.Peer!=Guid.Empty||_updateTransfers.Count>0||_historyWindow?.IsWorking==true)
        {
            UpdateStatusText.Text="请先结束文件传输或拖放，再重启更新。";return;
        }
        try
        {
            bool background=!IsVisible;
            _updateManager.WaitExitThenApplyUpdates(pending,silent:true,restart:true,restartArgs:background?new[]{"--background"}:Array.Empty<string>());
            _updateBusy=true;RefreshUpdateButtons();Quit();
        }
        catch(Exception){_updateBusy=false;UpdateStatusText.Text="无法启动更新，请稍后重试。";RefreshUpdateButtons();}
    }
    private void UpdateReleasePage_Click(object sender,RoutedEventArgs e)
    {
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Marigold1122/Necobridge/releases/latest"){UseShellExecute=true});}
        catch(Exception){UpdateStatusText.Text="无法打开发布页。";}
    }
}
