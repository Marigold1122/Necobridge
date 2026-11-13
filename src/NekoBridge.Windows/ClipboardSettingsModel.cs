using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NekoBridge.Core;
using NekoBridge.Windows.History;

namespace NekoBridge.Windows;

public sealed class ClipboardSettingsModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly Action _persist;
    private readonly HistoryStore _store;
    private readonly ClipboardSync _clipboard;
    private readonly Func<string, Task> _hotkey;
    private readonly CancellationToken _lifetime;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private string _maxItems;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ClipboardSettingsModel(AppSettings settings, Action persist, HistoryStore store, ClipboardSync clipboard, Func<string, Task> hotkey, CancellationToken lifetime)
    {
        _settings=settings; _persist=persist; _store=store; _clipboard=clipboard; _hotkey=hotkey; _lifetime=lifetime;
        _maxItems=settings.ClipboardHistoryMaxItems.ToString(CultureInfo.InvariantCulture);
    }
    private void Changed([CallerMemberName] string? name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    private void SetFlag(bool value, Func<bool> get, Action<bool> set, bool observe, [CallerMemberName] string? name=null)
    {
        bool previous=get(); if(previous==value)return;
        set(value);
        try { _persist(); }
        catch { set(previous); Changed(name); throw; }
        Changed(name);
        if(observe)_clipboard.EnabledChanged();
    }
    public bool SharingEnabled { get=>_settings.ClipboardEnabled; set=>SetFlag(value,()=>_settings.ClipboardEnabled,v=>_settings.ClipboardEnabled=v,true); }
    public bool HistoryEnabled { get=>_settings.ClipboardHistoryEnabled; set=>SetFlag(value,()=>_settings.ClipboardHistoryEnabled,v=>_settings.ClipboardHistoryEnabled=v,true); }
    public bool CaptureImages { get=>_settings.ClipboardHistoryCaptureImages; set=>SetFlag(value,()=>_settings.ClipboardHistoryCaptureImages,v=>_settings.ClipboardHistoryCaptureImages=v,false); }
    public bool CaptureFiles { get=>_settings.ClipboardHistoryCaptureFiles; set=>SetFlag(value,()=>_settings.ClipboardHistoryCaptureFiles,v=>_settings.ClipboardHistoryCaptureFiles=v,false); }
    public string HotkeyText=>_settings.ClipboardHistoryHotkey;
    public string MaxItems { get=>_maxItems; set { if(_maxItems==value)return; _maxItems=value; Changed(); } }
    public async Task ApplyAsync()
    {
        await _applyGate.WaitAsync(_lifetime);
        try
        {
            if(!int.TryParse(MaxItems,NumberStyles.None,CultureInfo.InvariantCulture,out int count)||count<0)
                throw new ArgumentException("条数请输入非负整数，0 表示不限条数。");
            int previous=_settings.ClipboardHistoryMaxItems;
            _settings.ClipboardHistoryMaxItems=count;
            try { _persist(); } catch { _settings.ClipboardHistoryMaxItems=previous; throw; }
            await _store.UpdateRetentionAsync(new HistoryRetention(count,_settings.ClipboardHistoryDays,_settings.ClipboardHistoryMaxBytes),_lifetime);
        }
        finally { _applyGate.Release(); }
    }
    public async Task SetHotkeyAsync(string shortcut)
    {
        try { await _hotkey(shortcut); }
        finally { Changed(nameof(HotkeyText)); }
    }
}
