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
    private bool _dialog;
    internal bool IsWorking=>_model.Busy;
    internal HistoryWindow(HistoryStore store,HistoryBridge bridge,NekoBridge.Core.AppSettings settings,Action persist,Func<string,Task> setHotkey,System.Threading.CancellationToken lifetime)
    {
        InitializeComponent();
        _=new HistoryWheelScroll(ClipList);
        _model=new HistoryViewModel(store,bridge,settings,persist,setHotkey,Finish,Error,Confirm,lifetime);
        Closing+=(_,args)=>{if(!lifetime.IsCancellationRequested){args.Cancel=true;Hide();}};
        DataContext=_model;
    }
    internal async Task OpenAsync(bool settings=false)
    {
        if(_model.Busy)return;
        _model.CancelRecording();
        _model.IsSettingsOpen=settings;
        await _model.RefreshAsync();
        Show();Activate();ClipList.Focus();
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
    private void OnMinimizeClick(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    private void OnMaximizeClick(object sender,RoutedEventArgs e)
    {
        if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);
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
    private void OnWindowDeactivated(object? sender,EventArgs e){if(!_dialog&&!_model.Busy&&!_model.IsSettingsOpen)Hide();}
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
