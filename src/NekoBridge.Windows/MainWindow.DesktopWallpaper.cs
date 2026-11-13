using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace NekoBridge.Windows;

public partial class MainWindow
{
    private string? _deviceWallpaperFile;
    private DateTime _deviceWallpaperStamp;
    private int _wallpaperRequest;
    private async void UpdateDeviceWallpaper()
    {
        var path=new StringBuilder(260);
        if(!SystemParametersInfo(0x73,(uint)path.Capacity,path,0)){Log("读取桌面壁纸失败。");return;}
        string file=path.ToString();
        try
        {
            DateTime stamp=string.IsNullOrWhiteSpace(file)?DateTime.MinValue:File.GetLastWriteTimeUtc(file);
            if(file==_deviceWallpaperFile&&stamp==_deviceWallpaperStamp)return;
            int request=++_wallpaperRequest;_deviceWallpaperFile=file;_deviceWallpaperStamp=stamp;
            if(string.IsNullOrWhiteSpace(file)){DeviceWallpaper.ImageSource=null;return;}
            var bitmap=await System.Threading.Tasks.Task.Run(()=>
            {
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                var result=new BitmapImage();result.BeginInit();result.CacheOption=BitmapCacheOption.OnLoad;
                result.DecodePixelWidth=640;result.StreamSource=stream;result.EndInit();result.Freeze();return result;
            });
            if(request==_wallpaperRequest&&!_quitting)DeviceWallpaper.ImageSource=bitmap;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _deviceWallpaperFile=null;DeviceWallpaper.ImageSource=null;
            Log("桌面壁纸载入失败，设备卡片不显示图片。");
        }
    }
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SystemParametersInfo(uint action,uint parameter,StringBuilder buffer,uint flags);
}
