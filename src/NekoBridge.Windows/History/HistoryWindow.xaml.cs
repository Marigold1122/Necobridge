using System.Linq;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NekoBridge.Windows.History;

public partial class HistoryWindow:Window
{
    private readonly HistoryViewModel _model;
    private readonly System.Threading.CancellationToken _lifetime;
    private readonly System.Collections.Generic.Dictionary<Image,System.Threading.CancellationTokenSource> _thumbnailLoads=new();
    private readonly System.Threading.SemaphoreSlim _thumbnailGate=new(2,2);
    private readonly System.Collections.Generic.HashSet<Image> _thumbnailImages=new();
    private bool _dialog;
    internal bool IsWorking=>_model.Busy;
    internal HistoryWindow(HistoryStore store,HistoryBridge bridge,ClipboardSettingsModel clipboardSettings,System.Threading.CancellationToken lifetime)
    {
        InitializeComponent();
        _=new HistoryWheelScroll(ClipList);
        _model=new HistoryViewModel(store,bridge,clipboardSettings,Finish,Error,Confirm,lifetime);
        Closing+=(_,args)=>{if(!lifetime.IsCancellationRequested){args.Cancel=true;Hide();}};
        DataContext=_model;
        _lifetime=lifetime;
        IsVisibleChanged+=(_,_)=>
        {
            if(!IsVisible)
            {
                foreach(var image in _thumbnailImages.Concat(_thumbnailLoads.Keys).Distinct().ToArray())ReleaseThumbnail(image);
                _model.ReleaseVisibleItems();
            }
        };
        HistoryVersionText.Text=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)??"";
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(PanelWindowMessages);
    }
    private nint PanelWindowMessages(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        // The quick panel remains edge-resizable, but has no minimize/maximize
        // action (including the caption double-click and system commands).
        if(message==0x00A3 || message==0x0112&&((wParam.ToInt64()&0xFFF0) is 0xF020 or 0xF030))handled=true;
        return 0;
    }
    internal async Task OpenAsync(bool settings=false)
    {
        if(_model.Busy)return;
        _model.CancelRecording();
        _model.IsSettingsOpen=settings;
        await _model.RefreshAsync();
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        // Restore onto a current monitor before showing a reused hidden window.
        if(WindowState==WindowState.Normal)
        {
            Width=Math.Max(MinWidth,Width);Height=Math.Max(MinHeight,Height);
            var area=System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle()).WorkingArea;
            var transform=System.Windows.PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice??System.Windows.Media.Matrix.Identity;
            var topLeft=transform.Transform(new Point(area.Left,area.Top));var bottomRight=transform.Transform(new Point(area.Right,area.Bottom));
            if(!double.IsNaN(Left))Left=Math.Clamp(Left,topLeft.X,Math.Max(topLeft.X,bottomRight.X-Width));
            if(!double.IsNaN(Top))Top=Math.Clamp(Top,topLeft.Y,Math.Max(topLeft.Y,bottomRight.Y-Height));
        }
        Show();Activate();ClipList.Focus();
    }
    private void ReleaseThumbnail(Image image)
    {
        if(_thumbnailLoads.Remove(image,out var request))request.Cancel();
        _thumbnailImages.Remove(image);image.Source=null;
    }
    private async void OnThumbnailLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is not Image image||!IsVisible)return;
        ReleaseThumbnail(image);
        if(image.DataContext is not HistoryItem{Kind:"Image"} item)return;
        using var stop=System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _thumbnailLoads[image]=stop;_thumbnailImages.Add(image);
        bool acquired=false;
        try
        {
            await _thumbnailGate.WaitAsync(stop.Token);acquired=true;
            var preview=await _model.ReadThumbnailAsync(item.Id,stop.Token);
            if(!stop.IsCancellationRequested&&IsVisible&&image.IsLoaded&&ReferenceEquals(image.DataContext,item))image.Source=preview;
        }
        catch(OperationCanceledException) { }
        catch(Exception ex){App.LogFailure(ex);}
        finally
        {
            if(acquired)_thumbnailGate.Release();
            if(_thumbnailLoads.TryGetValue(image,out var request)&&ReferenceEquals(request,stop))_thumbnailLoads.Remove(image);
        }
    }
    private void OnThumbnailUnloaded(object sender,RoutedEventArgs e){if(sender is Image image)ReleaseThumbnail(image);}
    private void OnThumbnailContextChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(sender is not Image image)return;
        ReleaseThumbnail(image);
        if(image.IsLoaded&&IsVisible)OnThumbnailLoaded(image,new RoutedEventArgs());
    }
    private async void OnHistoryScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(e.OriginalSource is not ScrollViewer viewer||viewer.ViewportHeight<=0)return;
        if(viewer.ScrollableHeight-viewer.VerticalOffset>160)return;
        try{await _model.LoadMoreAsync();}
        catch(OperationCanceledException) { }
        catch(Exception ex){Error(ex.Message);}
    }
    private Task Finish()
    {
        Hide();return Task.CompletedTask;
    }
    private void Error(string text){_dialog=true;try{MessageBox.Show(IsVisible?this:null,text,"剪贴板历史",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{_dialog=false;}}
    private bool Confirm(string text){_dialog=true;try{return MessageBox.Show(this,text,"剪贴板历史",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK;}finally{_dialog=false;}}
    private void OnMaxItemsPreviewTextInput(object sender,TextCompositionEventArgs e)
        =>e.Handled=e.Text.Any(c=>c<'0'||c>'9');

    private void OnMaxItemsPasting(object sender,DataObjectPastingEventArgs e)
    {
        if(!e.DataObject.GetDataPresent(DataFormats.UnicodeText)||e.DataObject.GetData(DataFormats.UnicodeText) is not string text||text.Any(c=>c<'0'||c>'9'))e.CancelCommand();
    }

    private void OnFilterChecked(object sender,RoutedEventArgs e)
    {
        if(DataContext is HistoryViewModel model&&sender is RadioButton button&&button.Tag is string filter)model.CurrentFilter=filter;
    }
    private void OnSettingsToggleClick(object sender,RoutedEventArgs e)
    {
        var command=_model.IsSettingsOpen?_model.CloseSettingsCommand:_model.OpenSettingsCommand;
        command.Execute(null);
    }
    private void OnHotkeyRecorderKeyDown(object sender,KeyEventArgs e)
    {
        if(!_model.Recording)return;
        e.Handled=true;var key=e.Key==Key.System?e.SystemKey:e.Key;
        if(key==Key.Escape){_model.CancelRecording();return;}
        if(key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;
        var modifiers=Keyboard.Modifiers;
        string shortcut=((modifiers&ModifierKeys.Control)!=0?"Ctrl+":"")+((modifiers&ModifierKeys.Alt)!=0?"Alt+":"")+((modifiers&ModifierKeys.Shift)!=0?"Shift+":"")+((modifiers&ModifierKeys.Windows)!=0?"Win+":"")+key;
        _= _model.RecordHotkey(shortcut);
    }
    private async void OnItemDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(_model.Busy||_model.SelectedItem==null)return;
        if(e.OriginalSource is DependencyObject origin)
        {
            for(var current=origin;current!=null;current=current is System.Windows.Media.Visual?System.Windows.Media.VisualTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current))
                if(current is System.Windows.Controls.Primitives.ButtonBase)return;
        }
        try{await _model.ActivateSelectedAsync();}catch(Exception ex){Error(ex.Message);}
    }
    private void OnPreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(_model.Recording)return;
        ICommand? command=null;
        if(e.Key==Key.Escape){e.Handled=true;if(_model.IsSettingsOpen)_model.CloseSettingsCommand.Execute(null);else Hide();return;}
        if(e.OriginalSource is TextBox)return;
        if(e.Key==Key.Enter)command=_model.CopyCommand;
        else if(e.Key==Key.Delete)command=_model.DeleteCommand;
        else if(e.Key==Key.D&&Keyboard.Modifiers==ModifierKeys.Control)command=_model.ToggleFavoriteCommand;
        else if(e.Key==Key.S&&Keyboard.Modifiers==ModifierKeys.Control)command=_model.SaveCopyCommand;
        if(command!=null){e.Handled=true;command.Execute(null);}
    }
    private void OnWindowDeactivated(object? sender,EventArgs e){if(!_dialog&&!_model.Busy)Hide();}
    private void OnWindowDrag(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is System.Windows.DependencyObject origin)
        {
            for(var current=origin;current!=null;current=current is System.Windows.Media.Visual?System.Windows.Media.VisualTreeHelper.GetParent(current):System.Windows.LogicalTreeHelper.GetParent(current))
                if(current is System.Windows.Controls.Primitives.ButtonBase or TextBox or ListBoxItem or System.Windows.Controls.Primitives.RangeBase)return;
        }
        if(e.ButtonState==MouseButtonState.Pressed)
        {try{DragMove();}catch(InvalidOperationException){}}
    }
}
