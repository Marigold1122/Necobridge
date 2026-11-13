using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NekoBridge.Windows;

public partial class MainWindow
{
    public ClipboardSettingsModel ClipboardSettings { get; set; } = null!;
    private bool _recordingClipboardHotkey;

    private void ArrangeNow_Click(object sender,RoutedEventArgs e)=>NavArr.IsChecked=true;

    public void InitializeClipboardSettings()
    {
        foreach(var check in new[]{ClipboardCheck,HistoryEnabledCheck,RecordImagesCheck,RecordFilesCheck})
            check.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
        MaxItemsBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        ClipboardHotkeyButton.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
    }
    private void Hotkey_Click(object sender,RoutedEventArgs e)
    {
        _recordingClipboardHotkey=true;
        ClipboardHotkeyButton.Content="按下组合键…";
        ClipboardHotkeyButton.Focus();
    }
    private async void Hotkey_KeyDown(object sender,KeyEventArgs e)
    {
        if(!_recordingClipboardHotkey)return;
        e.Handled=true;
        var key=e.Key==Key.System?e.SystemKey:e.Key;
        if(key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;
        _recordingClipboardHotkey=false;
        try
        {
            if(key!=Key.Escape)
            {
                var modifiers=Keyboard.Modifiers;
                string shortcut=((modifiers&ModifierKeys.Control)!=0?"Ctrl+":"")+((modifiers&ModifierKeys.Alt)!=0?"Alt+":"")+((modifiers&ModifierKeys.Shift)!=0?"Shift+":"")+((modifiers&ModifierKeys.Windows)!=0?"Win+":"")+key;
                await ClipboardSettings.SetHotkeyAsync(shortcut);
            }
        }
        catch(Exception ex){ShowNotice("快捷键设置失败",ex.Message,false);}
        finally{ClipboardHotkeyButton.SetBinding(ContentControl.ContentProperty,new System.Windows.Data.Binding("ClipboardSettings.HotkeyText"){Source=this});}
    }
    private void ClipboardHotkey_LostFocus(object sender,RoutedEventArgs e)
    {
        if(!_recordingClipboardHotkey)return;
        _recordingClipboardHotkey=false;
        ClipboardHotkeyButton.SetBinding(ContentControl.ContentProperty,new System.Windows.Data.Binding("ClipboardSettings.HotkeyText"){Source=this});
    }
    private void MaxItems_PreviewTextInput(object sender,TextCompositionEventArgs e)=>e.Handled=e.Text.Any(c=>c<'0'||c>'9');
    private void MaxItems_Pasting(object sender,DataObjectPastingEventArgs e)
    {
        if(!e.DataObject.GetDataPresent(DataFormats.UnicodeText)||e.DataObject.GetData(DataFormats.UnicodeText) is not string text||text.Any(c=>c<'0'||c>'9'))e.CancelCommand();
    }
    private async void MaxItems_LostFocus(object sender,RoutedEventArgs e)
    {
        if(!_uiReady||ClipboardSettings==null)return;
        try{await ClipboardSettings.ApplyAsync();}
        catch(Exception ex){ShowNotice("剪贴板设置失败",ex.Message,false);}
    }
}
