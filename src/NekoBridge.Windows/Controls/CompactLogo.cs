using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace NekoBridge.Windows.Controls;

// 24-DIP logos do not need the full 809x805 decoded asset. Keep enough pixels
// for 400% DPI, and release the image while its host is hidden/minimized.
public sealed class CompactLogo:Image
{
    private Window? _host;
    public CompactLogo()
    {
        Loaded+=(_,_)=>{_host=Window.GetWindow(this);if(_host!=null)_host.StateChanged+=HostStateChanged;Refresh();};
        Unloaded+=(_,_)=>{if(_host!=null)_host.StateChanged-=HostStateChanged;_host=null;Source=null;};
        IsVisibleChanged+=(_,_)=>Refresh();
    }
    private void HostStateChanged(object? sender,EventArgs e)=>Refresh();
    private void Refresh()
    {
        if(!IsLoaded||!IsVisible||_host?.WindowState==WindowState.Minimized){Source=null;return;}
        if(Source!=null)return;
        try
        {
            using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/NekoBridge;component/Assets/logo.png"))!.Stream;
            var image=new BitmapImage();image.BeginInit();image.DecodePixelWidth=96;image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();Source=image;
        }
        catch(Exception ex){App.LogFailure(ex);}
    }
}
