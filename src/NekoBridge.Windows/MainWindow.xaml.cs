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
        public Guid Id { get; set; } public string Name { get; set; } = ""; public string Res { get; set; } = ""; public bool Primary { get; set; }
        public Rect Px { get; set; } public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; }
        public Rect View => new Rect(X, Y, W, H);
    }

    public partial class MainWindow : Window
    {
        enum Side { None, Left, Top, Right, Bottom }
        Side _hot;
        void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();
        List<ScreenItem> _screens = new();

        // 等比缩放 + 居中，数据驱动而不是硬编码尺寸
        void Layout()
        {
            if (_screens.Count == 0 || Stage.ActualWidth < 8 || Stage.ActualHeight < 8) return;
            double minX = _screens.Min(s => s.Px.Left), minY = _screens.Min(s => s.Px.Top);
            double bw = Math.Max(1, _screens.Max(s => s.Px.Right) - minX), bh = Math.Max(1, _screens.Max(s => s.Px.Bottom) - minY);
            double s = Math.Min(Math.Min((Stage.ActualWidth - 64) / bw, (Stage.ActualHeight - 64) / bh), 1.0);
            double ox = (Stage.ActualWidth - bw * s) / 2, oy = (Stage.ActualHeight - bh * s) / 2;
            foreach (var sc in _screens)
            {
                sc.X = (sc.Px.Left - minX) * s + ox; sc.Y = (sc.Px.Top - minY) * s + oy;
                sc.W = sc.Px.Width * s; sc.H = sc.Px.Height * s;
            }
            if (Screens.ItemsSource is object src) { Screens.ItemsSource = null; Screens.ItemsSource = _screens; }
        }

        // ---------------- 越界语言：热区 / 方向 / 目标 ----------------
        void SetHot(Side side, bool drag = false)
        {
            if (side == Side.None) { EdgeZone.Visibility = EdgeTag.Visibility = Visibility.Collapsed; _hot = Side.None; return; }
            _hot = side;
            double t = 10, w = Stage.ActualWidth, h = Stage.ActualHeight;
            EdgeZone.Visibility = Visibility.Visible; EdgeZone.Fill = (Brush)FindResource("Lime"); EdgeZone.Opacity = 0.22;
            EdgeZone.Width = side is Side.Left or Side.Right ? t : w;
            EdgeZone.Height = side is Side.Top or Side.Bottom ? t : h;
            Canvas.SetLeft(EdgeZone, side == Side.Right ? w - t : 0);
            Canvas.SetTop(EdgeZone, side == Side.Bottom ? h - t : 0);
            ArrowRot.Angle = side switch { Side.Left => 180, Side.Top => -90, Side.Bottom => 90, _ => 0 };
            int i = _screens.FindIndex(sc => sc.Primary);
            int target = side == Side.Left ? i - 1 : side == Side.Right ? i + 1 : -1;
            EdgeText.Text = _router.Paused ? "共享已暂停" : target >= 0 && target < _screens.Count
                ? $"穿屏目标 · {_screens[target].Name}" : "此方向无远端设备";
            EdgeTag.Visibility = Visibility.Visible;
            EdgeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = EdgeTag.DesiredSize.Width, th = EdgeTag.DesiredSize.Height;
            double lx = side switch { Side.Left => t + 8, Side.Right => w - t - tw - 8, _ => (w - tw) / 2 };
            double ly = side switch { Side.Top => t + 8, Side.Bottom => h - t - th - 8, _ => (h - th) / 2 };
            Canvas.SetLeft(EdgeTag, Math.Max(8, lx)); Canvas.SetTop(EdgeTag, Math.Max(8, ly));
        }

        static Side Hit(Point p, double w, double h)
        {
            const double t = 16;
            if (p.Y <= t) return Side.Top; if (p.Y >= h - t) return Side.Bottom;
            if (p.X <= t) return Side.Left; if (p.X >= w - t) return Side.Right;
            return Side.None;
        }

        void Stage_MouseMove(object sender, MouseEventArgs e) => SetHot(Hit(e.GetPosition(Stage), Stage.ActualWidth, Stage.ActualHeight));
        void Stage_Leave(object sender, RoutedEventArgs e) => SetHot(Side.None);
        void Stage_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left: SetHot(Side.Left); break; case Key.Right: SetHot(Side.Right); break;
                case Key.Up: SetHot(Side.Top); break; case Key.Down: SetHot(Side.Bottom); break;
                case Key.Escape: SetHot(Side.None); break; default: return;
            }
            e.Handled = true;
        }

        void Stage_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            var p = e.GetPosition(Stage);
            SetHot(Hit(p, Stage.ActualWidth, Stage.ActualHeight) is Side.None ? Side.Bottom : Hit(p, Stage.ActualWidth, Stage.ActualHeight), true);
            e.Handled = true;
        }
        void Stage_Drop(object sender, DragEventArgs e) { SetHot(Side.None); e.Handled = true; }

        // ---------------- 导航 / 暂停 / 主题 ----------------
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelConn == null) return;
            var rb = (RadioButton)sender;
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
            if (_router.Paused) SetHot(Side.None);
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
