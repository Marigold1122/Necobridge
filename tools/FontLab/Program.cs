using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal sealed record RenderSettings(string Mode="Auto",string Formatting="Ideal",string Weight="Normal",string Font="江城圆体",bool Rounding=true,bool Snap=true);
internal static class Program
{
    [STAThread] static void Main()
    {
        var app=new Application();var window=new Window{Title="NekoBridge 字体现场对照 · 不运行共享功能",Width=1150,Height=820,MinWidth=900,MinHeight=620,WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=new SolidColorBrush(Color.FromRgb(251,248,244)),FontFamily=new FontFamily("Microsoft YaHei"),FontSize=13};
        var root=new DockPanel{Margin=new Thickness(24)};window.Content=root;
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12),Foreground=Brushes.DimGray};DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);
        var controls=new WrapPanel{Margin=new Thickness(0,0,0,18)};DockPanel.SetDock(controls,Dock.Top);root.Children.Add(controls);
        ComboBox Choice(string label,string[] choices){controls.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});var box=new ComboBox{ItemsSource=choices,SelectedIndex=0,MinWidth=110,Margin=new Thickness(0,0,16,8),Padding=new Thickness(6)};controls.Children.Add(box);return box;}
        var font=Choice("字体",new[]{"江城圆体","Microsoft YaHei","Segoe UI"});var mode=Choice("渲染",new[]{"Auto","Grayscale","ClearType","Aliased"});var formatting=Choice("排版",new[]{"Ideal","Display"});var weight=Choice("字重",new[]{"Normal","Medium","SemiBold"});
        var smallSize=Choice("首行字号",new[]{"12.5","14","16","18"});
        var round=new CheckBox{Content="布局取整",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,8)};var snap=new CheckBox{Content="像素吸附",IsChecked=true,VerticalAlignment=VerticalAlignment.Center};controls.Children.Add(round);controls.Children.Add(snap);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(scroll);var body=new StackPanel();scroll.Content=body;
        var selected=new StackPanel();body.Children.Add(selected);
        var compareTitle=new TextBlock{Text="上排 Ideal、下排 Display；每排依次为 Auto / Grayscale / ClearType。首行字号可直接切换。",Margin=new Thickness(0,24,0,16),Foreground=Brushes.DimGray};body.Children.Add(compareTitle);
        var grid=new UniformGridCompat();body.Children.Add(grid);
        var family=new FontFamily(new Uri("pack://application:,,,/"),"./Fonts/#JiangChengYuanTi");
        var sample=new[]{("连接　屏幕排列　设置",12.5),("同步剪贴板　深色主题　开机时启动",14d),("本机历史保留条数　1000　Alt+V",15d),("设备已连接。",46d),("圆角、曲线与斜线：电脑 ABC 0123456789",18d)};
        void Samples(Panel panel,FontFamily f,TextRenderingMode m,TextFormattingMode fmt,FontWeight fw,bool rounding,bool snapping)
        {
            panel.Children.Clear();panel.Background=window.Background;panel.UseLayoutRounding=rounding;panel.SnapsToDevicePixels=snapping;TextOptions.SetTextRenderingMode(panel,m);TextOptions.SetTextFormattingMode(panel,fmt);
            foreach(var (text,size) in sample)panel.Children.Add(new TextBlock{Text=text,FontFamily=f,FontSize=text.StartsWith("连接")?double.Parse((string)smallSize.SelectedItem,System.Globalization.CultureInfo.InvariantCulture):size,FontWeight=fw,Foreground=new SolidColorBrush(Color.FromRgb(27,33,48)),Margin=new Thickness(12,9,12,9),TextWrapping=TextWrapping.Wrap});
        }
        bool setting=false;
        void Refresh()
        {
            if(setting)return;
            var f=(string)font.SelectedItem=="江城圆体"?family:new FontFamily((string)font.SelectedItem);
            var m=Enum.Parse<TextRenderingMode>((string)mode.SelectedItem);var fmt=Enum.Parse<TextFormattingMode>((string)formatting.SelectedItem);var fw=(string)weight.SelectedItem switch{"Medium"=>FontWeights.Medium,"SemiBold"=>FontWeights.SemiBold,_=>FontWeights.Normal};
            Samples(selected,f,m,fmt,fw,round.IsChecked==true,snap.IsChecked==true);grid.Children.Clear();
            foreach(var comparisonFormat in new[]{TextFormattingMode.Ideal,TextFormattingMode.Display})
            foreach(var other in new[]{TextRenderingMode.Auto,TextRenderingMode.Grayscale,TextRenderingMode.ClearType})
            {
                var panel=new StackPanel{Margin=new Thickness(0,0,10,0)};Samples(panel,f,other,comparisonFormat,fw,round.IsChecked==true,snap.IsChecked==true);panel.Children.Insert(0,new TextBlock{Text=other+" / "+comparisonFormat,FontSize=14,FontFamily=new FontFamily("Microsoft YaHei"),Margin=new Thickness(12),FontWeight=FontWeights.SemiBold});grid.Children.Add(new Border{BorderThickness=new Thickness(1),BorderBrush=Brushes.LightGray,Child=panel,Margin=new Thickness(0,0,10,0)});
            }
            status.Text=$"当前：{font.SelectedItem} / {m} / {fmt} / {weight.SelectedItem} · DPI {VisualTreeHelper.GetDpi(window).PixelsPerInchX:F0}\n参数切换立即生效。该工具不会读取设置、历史或剪贴板，不注册键鼠钩子。可编辑同目录 render.json，窗口会自动重新加载。";
        }
        foreach(var box in new[]{font,mode,formatting,weight,smallSize})box.SelectionChanged+=(_,_)=>Refresh();round.Click+=(_,_)=>Refresh();snap.Click+=(_,_)=>Refresh();
        string config=Path.Combine(AppContext.BaseDirectory,"render.json");DateTime stamp=DateTime.MinValue;
        void ReadConfig()
        {
            try
            {
                var current=File.GetLastWriteTimeUtc(config);if(current==stamp)return;
                var settings=JsonSerializer.Deserialize<RenderSettings>(File.ReadAllText(config))!;
                if(!Enum.TryParse<TextRenderingMode>(settings.Mode,out _)||!Enum.TryParse<TextFormattingMode>(settings.Formatting,out _))return;
                setting=true;mode.SelectedItem=settings.Mode;formatting.SelectedItem=settings.Formatting;weight.SelectedItem=settings.Weight;font.SelectedItem=settings.Font;round.IsChecked=settings.Rounding;snap.IsChecked=settings.Snap;setting=false;stamp=current;Refresh();
            }
            catch(Exception ex){setting=false;status.Text="参数读取失败，保留当前显示："+ex.Message;}
        }
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(400)};timer.Tick+=(_,_)=>ReadConfig();window.Closed+=(_,_)=>timer.Stop();window.Loaded+=(_,_)=>{ReadConfig();Refresh();timer.Start();};app.Run(window);
    }
}
internal sealed class UniformGridCompat:System.Windows.Controls.Primitives.UniformGrid
{
    public UniformGridCompat(){Columns=3;}
}
