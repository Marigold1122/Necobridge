using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace NekoBridge.Windows;

public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app=new App();app.InitializeComponent();app.Run();
    }
    private Mutex? _single;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single=new Mutex(true,"Local\\NekoBridge-desktop",out bool created);
        if(!created){MessageBox.Show("NekoBridge 已在运行，请从系统托盘打开。","NekoBridge");Shutdown();return;}
        DispatcherUnhandledException+=(_,args)=>
        {
            LogFailure(args.Exception);
            MessageBox.Show($"操作失败：{args.Exception.Message}\n诊断已写入本地日志。","NekoBridge",MessageBoxButton.OK,MessageBoxImage.Error);
            args.Handled=true;
        };
        try
        {
            var window=new MainWindow();MainWindow=window;
            if(!e.Args.Contains("--background"))window.Show();
            else window.InitializeInBackground();
        }
        catch(Exception ex){LogFailure(ex);MessageBox.Show($"NekoBridge 启动失败：{ex.Message}","NekoBridge",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
    }
    internal static void LogFailure(Exception ex)
    {
        try
        {
            string directory=NekoBridge.Storage.DataLocation.Resolve();
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory,"errors.log"),$"{DateTimeOffset.Now:O} {ex}\n");
        }
        catch(Exception loggingError)
        {
            // Logging must not replace the original startup error if the
            // selected data drive is unavailable. Never switch data silently.
            System.Diagnostics.Trace.WriteLine(loggingError);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();base.OnExit(e);
    }
}
