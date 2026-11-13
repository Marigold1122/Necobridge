using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NekoBridge.Core;

namespace NekoBridge.Windows;
public partial class MainWindow
{
    private double _slotWidth,_slotHeight,_layoutScale=1,_layoutOriginX,_layoutOriginY;
    private Point _screenDragStart;
    private FrameworkElement? _dragElement;
    private Guid _selectedScreen;
    private int _dropColumn,_dropRow;
    private void Layout()
    {
        if(Stage==null||_screens.Count==0||Stage.ActualWidth<8||Stage.ActualHeight<8)return;
        if(_screenDrag!=Guid.Empty)return;
        _slotWidth=240;_slotHeight=135;
        int minColumn=_screens.Min(s=>s.Column),maxColumn=_screens.Max(s=>s.Column);
        int minRow=_screens.Min(s=>s.Row),maxRow=_screens.Max(s=>s.Row);
        double width=(maxColumn-minColumn+1)*_slotWidth,height=(maxRow-minRow+1)*_slotHeight;
        _layoutScale=Math.Min((Stage.ActualWidth-64)/width,(Stage.ActualHeight-64)/height);
        _layoutScale=Math.Max(.001,_layoutScale);
        _layoutOriginX=(Stage.ActualWidth-width*_layoutScale)/2-minColumn*_slotWidth*_layoutScale;
        _layoutOriginY=(Stage.ActualHeight-height*_layoutScale)/2-minRow*_slotHeight*_layoutScale;
        foreach(var screen in _screens)
        {
            screen.X=_layoutOriginX+screen.Column*_slotWidth*_layoutScale;
            screen.Y=_layoutOriginY+screen.Row*_slotHeight*_layoutScale;
            screen.SlotW=_slotWidth*_layoutScale;screen.SlotH=_slotHeight*_layoutScale;
            screen.W=screen.SlotW;screen.H=screen.SlotH;
        }
        Screens.ItemsSource=null;Screens.ItemsSource=_screens;
        DrawLayoutChannels();
    }
    private void DrawLayoutChannels()
    {
        LayoutLinks.Children.Clear();
        if(_router.Paused)return;
        foreach(var a in _screens.Where(s=>s.Online))
        foreach(var side in new[]{LayoutSide.Right,LayoutSide.Down})
        {
            bool horizontal=side==LayoutSide.Right;
            var target=LayoutSync.Neighbor(_state.Settings.LayoutCells,a.Id,side);
            var b=_screens.FirstOrDefault(s=>s.Id==target&&s.Online);
            if(b==null)continue;
            double x=a.X+(horizontal?a.SlotW:0),y=a.Y+(horizontal?0:a.SlotH);
            double extent=horizontal?a.SlotH:a.SlotW;
            // Two inset fluorescent rails; keep the original dark seam visible.
            foreach(double offset in new[]{-4d,4d})
            {
                double inset=Math.Min(6,extent/4);
                double x1=x+(horizontal?offset:inset),y1=y+(horizontal?inset:offset);
                double x2=x+(horizontal?offset:extent-inset),y2=y+(horizontal?extent-inset:offset);
                var glow=new Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=new SolidColorBrush(Color.FromRgb(177,216,93)),StrokeThickness=6,Opacity=.23,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,IsHitTestVisible=false,Effect=new System.Windows.Media.Effects.BlurEffect{Radius=4}};
                var rail=new Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=new SolidColorBrush(Color.FromRgb(162,201,78)),StrokeThickness=2,Opacity=.95,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,IsHitTestVisible=false};
                LayoutLinks.Children.Add(glow);LayoutLinks.Children.Add(rail);
            }
            double cx=x+(horizontal?0:extent/2),cy=y+(horizontal?extent/2:0);
            Point P(double along,double across)=>horizontal?new Point(cx+along,cy+across):new Point(cx-across,cy+along);
            var geometry=new StreamGeometry();
            using(var context=geometry.Open())
            {
                context.BeginFigure(P(-16,0),false,false);context.LineTo(P(16,0),true,false);
                context.BeginFigure(P(-9,-6),false,false);context.LineTo(P(-16,0),true,false);context.LineTo(P(-9,6),true,false);
                context.BeginFigure(P(9,-6),false,false);context.LineTo(P(16,0),true,false);context.LineTo(P(9,6),true,false);
            }
            geometry.Freeze();
            var arrow=new System.Windows.Shapes.Path{Data=geometry,Stroke=(Brush)FindResource("LimeDeep"),StrokeThickness=4,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,IsHitTestVisible=false};
            LayoutLinks.Children.Add(arrow);
            var hit=new Border{Width=44,Height=36,Background=Brushes.Transparent,ToolTip=$"{a.Name} {(horizontal?"↔":"↕")} {b.Name}"};
            ToolTipService.SetInitialShowDelay(hit,100);
            Canvas.SetLeft(hit,cx-22);Canvas.SetTop(hit,cy-18);LayoutLinks.Children.Add(hit);
        }
    }
    private void Screen_MouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left||sender is not FrameworkElement{DataContext:ScreenItem screen} element)return;
        _selectedScreen=screen.Id;_screenDrag=screen.Id;_screenDragStart=e.GetPosition(Stage);_dragElement=element;
        _dropColumn=screen.Column;_dropRow=screen.Row;
        if(!element.CaptureMouse()){_screenDrag=Guid.Empty;_dragElement=null;return;}
        LayoutLinks.Visibility=Visibility.Hidden;Stage.Focus();e.Handled=true;
    }
    private void MoveScreenPreview(object sender,MouseEventArgs e)
    {
        if(_screenDrag==Guid.Empty||_dragElement==null)return;
        if(e.LeftButton!=MouseButtonState.Pressed){FinishScreenDrag(false);return;}
        var screen=_screens.FirstOrDefault(s=>s.Id==_screenDrag);if(screen==null){FinishScreenDrag(false);return;}
        var position=e.GetPosition(Stage);var delta=position-_screenDragStart;
        _dragElement.RenderTransform=new TranslateTransform(delta.X,delta.Y);
        _dropColumn=screen.Column+(int)Math.Round(delta.X/(_slotWidth*_layoutScale));
        _dropRow=screen.Row+(int)Math.Round(delta.Y/(_slotHeight*_layoutScale));
        DropPreview.Width=screen.SlotW;DropPreview.Height=screen.SlotH;
        Canvas.SetLeft(DropPreview,_layoutOriginX+_dropColumn*_slotWidth*_layoutScale);
        Canvas.SetTop(DropPreview,_layoutOriginY+_dropRow*_slotHeight*_layoutScale);
        DropPreview.Visibility=Visibility.Visible;
    }
    private void Screen_MouseUp(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left)return;
        FinishScreenDrag(true);e.Handled=true;
    }
    private void Screen_LostCapture(object sender,MouseEventArgs e){if(_screenDrag!=Guid.Empty)FinishScreenDrag(false);}
    private void FinishScreenDrag(bool commit)
    {
        var id=_screenDrag;_screenDrag=Guid.Empty;
        var element=_dragElement;_dragElement=null;
        if(element!=null){element.RenderTransform=Transform.Identity;element.ReleaseMouseCapture();}
        DropPreview.Visibility=Visibility.Collapsed;LayoutLinks.Visibility=Visibility.Visible;
        if(commit&&id!=Guid.Empty)MoveCell(id,_dropColumn,_dropRow);
        Layout();
    }
    private void MoveCell(Guid id,int column,int row)
    {
        try
        {
            CommitSharedLayout(LayoutSync.Move(_state.Settings.LayoutCells,id,column,row));
        }
        catch(Exception ex){ShowNotice("屏幕排列保存失败",ex.Message,false);}
    }
    private void MoveSelectedScreen(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){FinishScreenDrag(false);e.Handled=true;return;}
        var selected=_state.Settings.LayoutCells.FirstOrDefault(c=>c.Id==_selectedScreen);
        if(selected==null)return;
        var delta=e.Key switch{Key.Left=>(-1,0),Key.Right=>(1,0),Key.Up=>(0,-1),Key.Down=>(0,1),_=>(0,0)};
        if(delta==(0,0))return;
        MoveCell(selected.Id,selected.Column+delta.Item1,selected.Row+delta.Item2);e.Handled=true;
    }
}
