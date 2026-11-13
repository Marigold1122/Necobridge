using System;
using System.Threading.Tasks;
using System.Windows;
using NekoBridge.Windows.History;

namespace NekoBridge.Windows;

public partial class MainWindow
{
    private async void ShowClipboardHistory()=>await OpenClipboardHistory(false);
    private async Task OpenClipboardHistory(bool settings)
    {
        if(_quitting)return;
        try
        {
            _historyWindow??=new HistoryWindow(_historyStore,_historyBridge,ClipboardSettings,_lifetime.Token);
            if(_historyWindow.IsWorking)return;
            if(_historyWindow.IsVisible&&!settings){_historyWindow.Hide();return;}
            await _historyWindow.OpenAsync(settings);
        }
        catch(Exception ex){ShowNotice("剪贴板历史",ex.Message,false);}
    }
    private Task SetHistoryHotkey(string shortcut)
    {
        if(_historyHotkey==null)throw new InvalidOperationException("快捷键尚未初始化。");
        string previous=_state.Settings.ClipboardHistoryHotkey;
        _historyHotkey.Set(shortcut);
        try{_state.Settings.ClipboardHistoryHotkey=_historyHotkey.Shortcut;_state.Save();}
        catch{_state.Settings.ClipboardHistoryHotkey=previous;_historyHotkey.Set(previous);throw;}
        return Task.CompletedTask;
    }
}
