using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NekoBridge.Windows.History;

internal sealed class HistoryWheelScroll
{
    private readonly ListBox _list;
    private readonly Stopwatch _elapsed=new();
    private ScrollViewer? _viewer;
    private double _from,_destination;
    private int _direction;
    private bool _running;
    internal HistoryWheelScroll(ListBox list)
    {
        _list=list;
        list.Loaded+=Loaded;
        list.Unloaded+=(_,_)=>{Stop();DetachViewer();};
        list.IsVisibleChanged+=(_,_)=>Stop();
        list.PreviewMouseWheel+=Wheel;
        list.PreviewMouseDown+=(_,_)=>Stop();
        list.PreviewTouchDown+=(_,_)=>Stop();
        list.PreviewKeyDown+=(_,_)=>Stop();
        list.SelectionChanged+=(_,_)=>Stop();
    }
    private void Loaded(object sender,RoutedEventArgs e)
    {
        DetachViewer();_viewer=FindViewer(_list);
        if(_viewer!=null)_viewer.ScrollChanged+=ScrollChanged;
    }
    private void DetachViewer(){if(_viewer!=null)_viewer.ScrollChanged-=ScrollChanged;_viewer=null;}
    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        if(root is ScrollViewer viewer)return viewer;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindViewer(VisualTreeHelper.GetChild(root,i)) is {} found)return found;
        return null;
    }
    private void Wheel(object sender,MouseWheelEventArgs e)
    {
        if(e.Handled||_viewer==null||Keyboard.Modifiers!=ModifierKeys.None||e.Delta==0)return;
        for(DependencyObject? item=e.OriginalSource as DependencyObject;item!=null&&item!=_list;item=item is Visual?VisualTreeHelper.GetParent(item):LogicalTreeHelper.GetParent(item))
            if(item is RangeBase||(item is ScrollViewer nested&&nested!=_viewer))return;
        int lines=SystemParameters.WheelScrollLines;
        if(lines==0||_viewer.ScrollableHeight<=0)return;
        e.Handled=true;
        int direction=Math.Sign(e.Delta);
        double current=_viewer.VerticalOffset;
        if(!_running||direction!=_direction)_destination=current;
        _direction=direction;
        double unit=lines<0?_viewer.ViewportHeight:lines*Math.Max(1,_list.FontSize*1.2);
        _destination=Math.Clamp(_destination-e.Delta*unit/120d,0,_viewer.ScrollableHeight);
        if(!SystemParameters.ClientAreaAnimation){Stop();_viewer.ScrollToVerticalOffset(_destination);return;}
        _from=current;_elapsed.Restart();
        if(_running)return;
        _running=true;CompositionTarget.Rendering+=Frame;
    }
    private void Frame(object? sender,EventArgs e)
    {
        if(_viewer==null||!_list.IsVisible){Stop();return;}
        double progress=Math.Min(1,_elapsed.Elapsed.TotalMilliseconds/100d);
        double target=Math.Clamp(_destination,0,_viewer.ScrollableHeight);
        _viewer.ScrollToVerticalOffset(_from+(target-_from)*(1-Math.Pow(1-progress,3)));
        if(progress>=1)Stop();
    }
    private void ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(e.OriginalSource!=_viewer)return;
        if(e.ExtentHeightChange!=0||e.ViewportHeightChange!=0)Stop();
    }
    private void Stop()
    {
        if(_running)CompositionTarget.Rendering-=Frame;
        _running=false;_direction=0;_elapsed.Stop();
    }
}
