using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoBridge.Core;

namespace NekoBridge.Windows.History;

internal sealed class HistoryItem(HistoryEntry entry)
{
    internal HistoryEntry Entry{get;}=entry;
    public Guid Id=>Entry.Id;
    public string Kind=>Entry.Kind switch{"image"=>"Image","files"=>"Files",_=>"Text"};
    public string Title=>Entry.Kind switch{"image"=>"图片","files"=>Entry.FileSummary??(Entry.Paths?.FirstOrDefault() is {} p?Path.GetFileName(p):"文件"),_=>new string((Entry.Text??"").Take(100).ToArray()).Replace('\r',' ').Replace('\n',' ')};
    public string? Text=>Entry.Text;
    public bool IsFavorite=>Entry.IsFavorite;
    public string SourceLabel=>Entry.SourceName;
    public DateTimeOffset CreatedAt=>Entry.LastCopiedAt;
    public string TimeLabel=>Entry.LastCopiedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string[] Files=>Entry.SavedPaths??Entry.Paths??new[]{Entry.FileSummary??"来源文件（需要来源电脑在线，且分享尚未过期）"};
    public string Meta=>Entry.Kind=="files"?(Entry.SavedPaths!=null?"已保存离线副本":"文件引用：来源需可用；过期后请重新复制"):$"{Entry.StoredBytes/1024d:F1} KB";
    public ImageSource? PreviewPath{get;internal set;}
    public ImageSource? ThumbPath=>PreviewPath;
}

internal sealed class HistoryViewModel:ObservableModel
{
    private readonly ClipboardSettingsModel _shared;
    private readonly HistoryStore _store;
    private readonly HistoryBridge _bridge;
    private readonly Func<Task> _close;
    private readonly Action<string> _error;
    private readonly Func<string,bool> _confirm;
    private readonly CancellationToken _lifetime;
    private long _query;
    private string _search="",_filter="全部",_hotkey;
    private bool _settingsOpen;
    private bool _hasMore;
    private int _loaded;
    private bool _loadingPage;
    private int _refreshing;
    public bool HasMore{get=>_hasMore;private set{_hasMore=value;Changed();}}
    private HistoryItem? _selected;
    private readonly System.Collections.Generic.List<HistoryCommand> _commands=[];
    internal bool Busy{get;private set;}
    private void SetBusy(bool value){Busy=value;foreach(var command in _commands)command.Notify();}
    public ObservableCollection<HistoryItem> Items{get;}=[];
    public ObservableCollection<HistoryItem> FilteredItems{get;}=[];
    public string[] Filters{get;}={"全部","文本","图片","文件","收藏"};
    public string SearchText{get=>_search;set{if(_search==value)return;_search=value;Changed();RefreshObserved();}}
    public string CurrentFilter{get=>_filter;set{if(_filter==value)return;_filter=value;Changed();RefreshObserved();}}
    public HistoryItem? SelectedItem{get=>_selected;set{_selected=value;Changed();}}
    public bool IsEmpty=>Items.Count==0;
    public bool IsFilteredEmpty=>Items.Count>0&&FilteredItems.Count==0;
    public bool IsSettingsOpen{get=>_settingsOpen;set{_settingsOpen=value;Changed();}}
    public string HotkeyText{get=>Recording?_hotkey:_shared.HotkeyText;private set{_hotkey=value;Changed();}}
    internal bool Recording{get;private set;}
    public string MaxItems{get=>_shared.MaxItems;set=>_shared.MaxItems=value;}
    public bool SharingEnabled{get=>_shared.SharingEnabled;set=>_shared.SharingEnabled=value;}
    public bool HistoryEnabled{get=>_shared.HistoryEnabled;set=>_shared.HistoryEnabled=value;}
    public bool CaptureImages{get=>_shared.CaptureImages;set=>_shared.CaptureImages=value;}
    public bool CaptureFiles{get=>_shared.CaptureFiles;set=>_shared.CaptureFiles=value;}
    public ICommand ClearSearchCommand{get;}
    public ICommand OpenSettingsCommand{get;}
    public ICommand CloseSettingsCommand{get;}
    public ICommand CloseCommand{get;}
    public ICommand BeginRecordingCommand{get;}
    public ICommand ClearAllCommand{get;}
    public ICommand CopyCommand{get;}
    public ICommand SaveCopyCommand{get;}
    public ICommand DeleteCommand{get;}
    public ICommand ToggleFavoriteCommand{get;}

    internal HistoryViewModel(HistoryStore store,HistoryBridge bridge,ClipboardSettingsModel clipboardSettings,Func<Task> close,Action<string> error,Func<string,bool> confirm,CancellationToken lifetime)
    {
        _shared=clipboardSettings;
        _shared.PropertyChanged+=(_,e)=>Changed(e.PropertyName);
        _store=store;_bridge=bridge;_close=close;_error=error;_confirm=confirm;_lifetime=lifetime;_hotkey=_shared.HotkeyText;
        HistoryCommand Command(Func<object?,Task> action)
        {
            var command=new HistoryCommand(async parameter=>{if(Busy)return;SetBusy(true);try{await action(parameter);}finally{SetBusy(false);}},ex=>_error(ex.Message),_=>!Busy);
            _commands.Add(command);return command;
        }
        ClearSearchCommand=Command(_=>{SearchText="";return Task.CompletedTask;});
        OpenSettingsCommand=Command(_=>{IsSettingsOpen=true;return Task.CompletedTask;});
        CloseSettingsCommand=Command(async _=>
        {
            await _shared.ApplyAsync();
            IsSettingsOpen=false;await RefreshAsync();
        });
        CloseCommand=Command(_=>_close());
        BeginRecordingCommand=Command(_=>{Recording=true;HotkeyText="按下组合键…";return Task.CompletedTask;});
        ClearAllCommand=Command(async _=>{if(!_confirm("清空本机全部剪贴板历史（包括收藏和离线副本）？不会清空其他电脑或删除来源文件。"))return;await _store.ClearAsync(true,lifetime);await RefreshAsync();});
        CopyCommand=Command(p=>Restore(p as HistoryItem));
        SaveCopyCommand=Command(async p=>{if((p as HistoryItem??SelectedItem) is not {} item)return;if(item.Entry.Kind!="files"){_error("文字和图片已持久保存；只有文件记录需要另存离线副本。");return;}await _bridge.SaveFileCopyAsync(item.Entry,lifetime);await RefreshAsync();});
        DeleteCommand=Command(async p=>{if((p as HistoryItem??SelectedItem) is not {} item)return;await _store.RemoveAsync(item.Id,lifetime);await RefreshAsync();});
        ToggleFavoriteCommand=Command(async p=>{if((p as HistoryItem??SelectedItem) is not {} item)return;await _store.SetFavoriteAsync(item.Id,!item.IsFavorite,lifetime);await RefreshAsync();});
    }
    internal async Task RecordHotkey(string shortcut)
    {
        if(!Recording)return;
        try{await _shared.SetHotkeyAsync(shortcut);Recording=false;HotkeyText=_shared.HotkeyText;}
        catch(Exception ex){Recording=false;HotkeyText=_shared.HotkeyText;_error(ex.Message);}
    }
    internal void CancelRecording(){Recording=false;HotkeyText=_shared.HotkeyText;}
    internal async Task ActivateSelectedAsync()
    {
        if(Busy)return;SetBusy(true);
        try{await Restore();}finally{SetBusy(false);}
    }
    private async Task Restore(HistoryItem? requested=null)
    {
        if((requested??SelectedItem) is not {} item)return;
        await _bridge.RestoreAsync(item.Entry,_lifetime);await _close();
    }
    private async void RefreshObserved(){try{await RefreshAsync();}catch(OperationCanceledException){}catch(Exception ex){_error(ex.Message);}}
    internal async Task RefreshAsync()
    {
        _refreshing++;
        try{await LoadPageAsync(true);}finally{_refreshing--;}
    }
    internal async Task LoadMoreAsync()
    {
        if(Busy||_refreshing>0||_loadingPage||!HasMore)return;
        _loadingPage=true;
        try{await LoadPageAsync(false);}finally{_loadingPage=false;}
    }
    private async Task LoadPageAsync(bool reset)
    {
        long query=reset?++_query:_query;
        int offset=reset?0:_loaded;
        string? kind=CurrentFilter switch{"文本"=>"text","图片"=>"image","文件"=>"files",_=>null};
        var entries=await _store.SearchPageAsync(SearchText,CurrentFilter=="收藏",kind,100,offset,_lifetime);
        if(query!=_query)return;
        Guid selected=SelectedItem?.Id??Guid.Empty;
        var loaded=new System.Collections.Generic.List<HistoryItem>();
        foreach(var entry in entries)
        {
            var item=new HistoryItem(entry);
            if(entry.Kind=="image")
            {
                byte[]? image=await _store.ReadImageAsync(entry.Id,_lifetime);
                if(query!=_query)return;
                if(image!=null){using var stream=new MemoryStream(image,false);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.DecodePixelWidth=320;bitmap.EndInit();bitmap.Freeze();item.PreviewPath=bitmap;}
            }
            loaded.Add(item);
        }
        if(query!=_query)return;
        if(reset)Items.Clear();
        foreach(var item in loaded)if(!Items.Any(i=>i.Id==item.Id))Items.Add(item);
        _loaded=offset+entries.Count;HasMore=entries.Count==100;
        if(reset)ApplyFilter();
        else
        {
            foreach(var item in loaded)if(!FilteredItems.Any(i=>i.Id==item.Id))FilteredItems.Add(item);
            Changed(nameof(IsEmpty));Changed(nameof(IsFilteredEmpty));
        }
        SelectedItem=FilteredItems.FirstOrDefault(i=>i.Id==selected)??FilteredItems.FirstOrDefault();
    }
    private void ApplyFilter()
    {
        var selected=SelectedItem?.Id;FilteredItems.Clear();
        foreach(var item in Items.Where(i=>CurrentFilter switch{"文本"=>i.Kind=="Text","图片"=>i.Kind=="Image","文件"=>i.Kind=="Files","收藏"=>i.IsFavorite,_=>true}))FilteredItems.Add(item);
        SelectedItem=FilteredItems.FirstOrDefault(i=>i.Id==selected)??FilteredItems.FirstOrDefault();Changed(nameof(IsEmpty));Changed(nameof(IsFilteredEmpty));
    }
}
