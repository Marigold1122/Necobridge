param([Parameter(Mandatory=$true)][int]$PreviewProcessId,[string]$Output='D:\code\nekobridge\design\preview\connection.png')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PreviewCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[PreviewCapture]::SetProcessDPIAware() | Out-Null
$p=Get-Process -Id $PreviewProcessId
$p.Refresh()
$hwnd=$p.MainWindowHandle
if($hwnd -eq [IntPtr]::Zero){throw 'Preview has no window'}
[PreviewCapture]::ShowWindow($hwnd,9) | Out-Null
[PreviewCapture]::SetForegroundWindow($hwnd) | Out-Null
$r=New-Object PreviewCapture+Rect
if(-not [PreviewCapture]::GetWindowRect($hwnd,[ref]$r)){throw 'Cannot read preview window bounds'}
$bitmap=New-Object System.Drawing.Bitmap ($r.Right-$r.Left),($r.Bottom-$r.Top)
$graphics=[System.Drawing.Graphics]::FromImage($bitmap)
$dc=$graphics.GetHdc()
try{if(-not [PreviewCapture]::PrintWindow($hwnd,$dc,2)){throw 'Window rendering capture failed'}}finally{$graphics.ReleaseHdc($dc)}
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Output)) | Out-Null
$bitmap.Save($Output,[System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose();$bitmap.Dispose()
Write-Output $Output
