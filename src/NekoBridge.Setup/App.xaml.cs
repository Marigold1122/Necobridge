using System.Windows;
namespace NekoBridge.Setup;
public partial class App:Application
{
    protected override void OnStartup(StartupEventArgs e){base.OnStartup(e);var window=new InstallWindow();MainWindow=window;window.Show();}
}
