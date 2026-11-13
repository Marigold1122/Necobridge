using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace NekoBridge.Windows
{
    public class ScreenItem
    {
        public Guid Id{get;set;} public string Name{get;set;}="";public string Res{get;set;}="";
        public bool Primary{get;set;} public bool Online{get;set;} public int Column{get;set;} public int Row{get;set;}
        public int PixelWidth{get;set;} public int PixelHeight{get;set;}
        public double X{get;set;}public double Y{get;set;}public double W{get;set;}public double H{get;set;}
        public double SlotW{get;set;}public double SlotH{get;set;}
        public bool HasPadding=>W<SlotW-.1||H<SlotH-.1;
        public string Role=>Primary?"本机 · 虚拟桌面":Online?"远端设备":"设备离线";
        public Rect View=>new Rect(X,Y,W,H);
    }
    public partial class MainWindow : Window
    {
        List<ScreenItem> _screens=new();
        void Stage_SizeChanged(object sender,SizeChangedEventArgs e)=>Layout();
        void Stage_Leave(object sender,RoutedEventArgs e) { }
        void Stage_KeyDown(object sender,KeyEventArgs e)=>MoveSelectedScreen(sender,e);
        void Stage_MouseMove(object sender,MouseEventArgs e)=>MoveScreenPreview(sender,e);
        void Stage_DragOver(object sender,DragEventArgs e){e.Effects=DragDropEffects.None;e.Handled=true;}
        void Stage_Drop(object sender,DragEventArgs e){e.Effects=DragDropEffects.None;e.Handled=true;}

        // ---------------- 导航 / 暂停 / 主题 ----------------
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelConn == null) return;
            var rb = (RadioButton)sender;
            MainContent.MaxWidth=rb==NavConn?double.PositiveInfinity:960;
            PanelConn.Visibility = rb == NavConn ? Visibility.Visible : Visibility.Collapsed;
            PanelArr.Visibility = rb == NavArr ? Visibility.Visible : Visibility.Collapsed;
            PanelSet.Visibility = rb == NavSet ? Visibility.Visible : Visibility.Collapsed;
            PageNum.Text = (string)rb.Tag; PageTitle.Text = (string)rb.Content;
            if (rb == NavArr) { Layout(); Stage.Focus(); }
        }

        void Pause_Changed(object? sender, RoutedEventArgs? e)
        {
            if (!_uiReady) return;
            _router.SetPaused(PauseToggle.IsChecked == true);
            CancelDrag(); RefreshStatus();
            Layout();
        }

        void Theme_Changed(object sender, RoutedEventArgs e) => ApplyTheme(DarkCheck.IsChecked == true);

        void ApplyTheme(bool dark)
        {
            var m = (dark
                ? new[] { ("Ink", "#F1EBE4"), ("Ink70", "#B9B4AE"), ("Muted", "#7C7A80"), ("Paper", "#171B24"), ("Paper2", "#1F2531"), ("Line", "#2C3444"), ("Lime", "#AFDC3C"), ("LimeDeep", "#C6EC6A"), ("LimeWash", "#232D1E"), ("Vein", "#D4736E") }
                : new[] { ("Ink", "#1B2130"), ("Ink70", "#4E5464"), ("Muted", "#8B8A93"), ("Paper", "#FBF8F4"), ("Paper2", "#F1EBE4"), ("Line", "#E0D8CF"), ("Lime", "#AFDC3C"), ("LimeDeep", "#58790F"), ("LimeWash", "#EFF7DB"), ("Vein", "#9E3B38") });
            foreach (var (key, value) in m) Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            Resources["GridBrush"] = MakeGrid();
            Background = (Brush)Resources["Paper"];
            Layout();
        }

        DrawingBrush MakeGrid()
        {
            var pen = new Pen((Brush)Resources["Line"], 0.5) { };
            var g = new GeometryDrawing { Geometry = new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(24, 0)), new LineGeometry(new Point(0, 0), new Point(0, 24)) } }, Pen = pen };
            return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 24, 24), ViewportUnits = BrushMappingMode.Absolute };
        }

        // ---------------- 窗口铬 ----------------
        void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { Max_Click(sender, e); return; }
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Window_StateChanged(object? sender, EventArgs e)
        {
            BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxPath.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IcoRestore" : "IcoMax");
        }

        const int WM_NCHITTEST = 0x0084, HTMAXBUTTON = 10, HTCLIENT = 1;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        void Window_SourceInitialized(object? sender, EventArgs e) =>
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(HitTest);

        // 让 Win11 Snap Layouts 在悬停最大化按钮时可用
        IntPtr HitTest(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            var p = new POINT { X = (short)(l.ToInt64() & 0xFFFF), Y = (short)(l.ToInt64() >> 16) };
            var pt = PointFromScreen(new Point(p.X, p.Y));
            if (pt.X >= MaxBtn.TranslatePoint(new Point(0, 0), this).X && pt.X <= MaxBtn.TranslatePoint(new Point(MaxBtn.ActualWidth, 0), this).X
                && pt.Y >= 0 && pt.Y <= 40)
            { handled = true; return (IntPtr)HTMAXBUTTON; }
            return IntPtr.Zero;
        }
    }
}
