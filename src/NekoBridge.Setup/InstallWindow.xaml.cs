using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NekoBridge.Storage;

namespace NekoBridge.Setup;
public partial class InstallWindow:Window
{
    private string _source="",_installed="";
    private bool _busy,_finished;
    public InstallWindow()
    {
        InitializeComponent();
        SetupVersionText.Text=Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)+" · Windows x64";
        try
        {
            _source=DataLocation.Resolve();InstallPath.Text=DataLocation.InstallDirectory();DataPath.Text=_source;
            ExistingDataText.Text=DataMigration.HasData(_source)?"检测到原数据："+_source+"\n更换数据位置时会复制原记录，原目录先保留，不自动删除。":"可分别选择程序和数据目录。设备证书仍保存在当前 Windows 用户的证书存储中。";
        }
        catch(Exception ex){StatusText.Text="无法读取原目录设置："+ex.Message;InstallButton.IsEnabled=false;}
    }
    private void BrowseInstall_Click(object sender,RoutedEventArgs e)=>Browse(InstallPath);
    private void BrowseData_Click(object sender,RoutedEventArgs e)=>Browse(DataPath);
    private void Browse(System.Windows.Controls.TextBox box)
    {
        using var dialog=new System.Windows.Forms.FolderBrowserDialog{Description="选择文件夹",UseDescriptionForTitle=true,ShowNewFolderButton=true};
        if(Directory.Exists(box.Text))dialog.SelectedPath=box.Text;
        if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)box.Text=dialog.SelectedPath;
    }
    private async void Install_Click(object sender,RoutedEventArgs e)
    {
        if(_busy)return;
        if(_finished){LaunchInstalled();return;}
        bool owns=false;Mutex? mutex=null;
        try
        {
            var install=DataLocation.Normalize(InstallPath.Text.Trim());var data=DataLocation.Normalize(DataPath.Text.Trim());
            DataMigration.Validate(_source,data,install);
            ValidateInstallDirectory(install);
            mutex=new Mutex(false,DataLocation.InstanceMutex);
            try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
            if(!owns)throw new IOException("NekoBridge 正在运行。请从系统托盘退出后再安装，不会强制结束程序。");
            _busy=true;DirectoryFields.IsEnabled=false;InstallButton.IsEnabled=CancelButton.IsEnabled=false;InstallProgress.Visibility=Visibility.Visible;
            StatusText.Text="正在准备数据…";
            // No registry switch until the native install has succeeded. Keep a
            // verified copy before letting the package installer touch old files.
            await Task.Run(()=>{DataMigration.ProbeWritable(install);DataMigration.Prepare(_source,data,install);});
            StatusText.Text="正在安装程序…";
            await InstallPayload(install);
            StatusText.Text="正在保存目录设置…";
            DataLocation.Save(data,install);
            RepairStartup(data,install);
            _installed=install;_finished=true;InstallButton.Content="启动 NekoBridge";CancelButton.Content="完成";
            CompletedPaths.Text="程序："+install+"\n数据："+data;
            StatusText.Text="安装完成。";
        }
        catch(Exception ex)
        {
            StatusText.Text="安装未完成："+ex.Message+"\n原数据未删除。若新目录已复制数据，请保留它，不要手动清空原目录。";
        }
        finally
        {
            // Mutex is acquired/released on this WPF dispatcher thread, never
            // moved into Task.Run (Windows mutex ownership is thread-affine).
            if(owns)mutex!.ReleaseMutex();mutex?.Dispose();
            _busy=false;InstallProgress.Visibility=Visibility.Collapsed;
            InstallButton.IsEnabled=CancelButton.IsEnabled=true;DirectoryFields.IsEnabled=!_finished;
        }
    }
    private static void ValidateInstallDirectory(string path)
    {
        DataLocation.EnsureNoLinks(path);
        if(File.Exists(path))throw new IOException("安装位置是文件，不是目录。");
        if(Directory.Exists(path)&&System.Linq.Enumerable.Any(Directory.EnumerateFileSystemEntries(path)))
        {
            var manifest=Path.Combine(path,"current","sq.version");
            if(!File.Exists(Path.Combine(path,"Update.exe"))||!File.Exists(manifest)||File.Exists(Path.Combine(path,".portable")))
                throw new IOException("安装位置已有其他文件。请选择空文件夹或原 NekoBridge 安装目录，不能覆盖便携版。");
            var document=System.Xml.Linq.XDocument.Load(manifest);
            var metadata=document.Root?.Element(document.Root.Name.Namespace+"metadata");
            if(metadata?.Element(metadata.Name.Namespace+"id")?.Value!="NekoBridge")
                throw new IOException("此目录属于其他应用，不能安装到这里。");
        }
    }
    private static async Task InstallPayload(string path)
    {
        using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("NekoBridge.Setup.Payload.exe")??throw new IOException("安装包不完整，缺少程序载荷。");
        string temporary=Path.Combine(Path.GetTempPath(),"NekoBridge-Install-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        var exe=Path.Combine(temporary,"Setup.exe");
        await using(var output=new FileStream(exe,FileMode.CreateNew,FileAccess.Write,FileShare.None))await resource.CopyToAsync(output);
        var start=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=temporary};
        start.ArgumentList.Add("--silent");start.ArgumentList.Add("--installto");start.ArgumentList.Add(path);
        using var process=Process.Start(start)??throw new IOException("无法启动安装引擎。");await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException("安装引擎返回错误 "+process.ExitCode+"。请检查目录权限与磁盘空间。");
        if(!File.Exists(Path.Combine(path,"NekoBridge.exe"))||!File.Exists(Path.Combine(path,"Update.exe")))throw new IOException("安装引擎未生成完整程序。");
        // Remove only the exact payload created above, not an entire mutable tree.
        File.Delete(exe);try{Directory.Delete(temporary,false);}catch(IOException) { }
    }
    private static void RepairStartup(string data,string install)
    {
        var settings=Path.Combine(data,"settings.json");if(!File.Exists(settings))return;
        using var json=JsonDocument.Parse(File.ReadAllText(settings));
        if(!json.RootElement.TryGetProperty("StartWithWindows",out var value)||!value.GetBoolean())return;
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.SetValue("NekoBridge","\""+Path.Combine(install,"NekoBridge.exe")+"\" --background");
    }
    private void LaunchInstalled()
    {
        try{Process.Start(new ProcessStartInfo(Path.Combine(_installed,"NekoBridge.exe")){UseShellExecute=true});Close();}
        catch(Exception ex){StatusText.Text="无法启动程序："+ex.Message;}
    }
    private void Cancel_Click(object sender,RoutedEventArgs e){if(!_busy)Close();}
    private void Window_Closing(object? sender,CancelEventArgs e){if(_busy)e.Cancel=true;}
}
