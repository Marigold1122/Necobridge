using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace NekoBridge.Windows.History;

internal sealed class HistoryHotkey : IDisposable
{
    private const int Id=0x4E48;
    private readonly HwndSource _source;
    private readonly Action _show;
    private bool _registered;
    internal string Shortcut{get;private set;}="";
    internal HistoryHotkey(HwndSource source,Action show){_source=source;_show=show;source.AddHook(Messages);}
    internal void Set(string shortcut)
    {
        var (modifiers,key,canonical)=Parse(shortcut);
        if(canonical==Shortcut&&_registered)return;
        string previous=Shortcut;
        if(_registered){UnregisterHotKey(_source.Handle,Id);_registered=false;}
        if(RegisterHotKey(_source.Handle,Id,modifiers|0x4000,key)){_registered=true;Shortcut=canonical;return;}
        int error=Marshal.GetLastWin32Error();
        if(previous.Length>0)
        {
            var(oldModifiers,oldKey,_)=Parse(previous);
            _registered=RegisterHotKey(_source.Handle,Id,oldModifiers|0x4000,oldKey);
        }
        throw new Win32Exception(error,"快捷键被占用或无法注册，请换一个组合键。");
    }
    internal static (uint Modifiers,uint Key,string Canonical) Parse(string shortcut)
    {
        string[] tokens=shortcut.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Length<2||tokens.Length>5)throw new ArgumentException("请输入组合键，例如 Alt+V 或 Ctrl+Shift+V。");
        uint mods=0;
        for(int i=0;i<tokens.Length-1;i++)mods|=tokens[i].ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL"=>2u,"ALT"=>1u,"SHIFT"=>4u,"WIN" or "WINDOWS"=>8u,_=>throw new ArgumentException("不支持的修饰键。")
        };
        string name=tokens[^1];
        if(!Enum.TryParse<Key>(name,true,out var key))throw new ArgumentException("不支持的快捷键，请使用字母、数字或功能键。");
        int vk=KeyInterop.VirtualKeyFromKey(key);
        if(vk==0||key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException("快捷键需要包含普通按键。");
        if((mods&3)==3&&key is Key.Home or Key.Pause)throw new ArgumentException("该组合键用于紧急返回本机，请选择其他快捷键。");
        string canonical=((mods&2)!=0?"Ctrl+":"")+((mods&1)!=0?"Alt+":"")+((mods&4)!=0?"Shift+":"")+((mods&8)!=0?"Win+":"")+key;
        return(mods,(uint)vk,canonical);
    }
    private nint Messages(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x312&&wParam==Id){handled=true;_show();}return 0;
    }
    public void Dispose(){if(_registered)UnregisterHotKey(_source.Handle,Id);_registered=false;_source.RemoveHook(Messages);}
    [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]private static extern bool UnregisterHotKey(nint hwnd,int id);
}
