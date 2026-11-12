$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = 'D:\code\nekobridge\src\NekoBridge.Windows\Assets'
[System.IO.Directory]::CreateDirectory($root) | Out-Null
Copy-Item -LiteralPath 'C:\Users\Marigold\Desktop\11766808.png' -Destination (Join-Path $root 'wallpaper.png')
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#171A38'))
$pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#B7B3F2')),4
$graphics.DrawEllipse($pen,9,14,46,18)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddBezier(16,51,25,36,39,36,48,51)
$graphics.DrawPath($pen,$path)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.File]::Create((Join-Path $root 'nekobridge.ico'))
$icon.Save($stream)
$stream.Dispose(); $icon.Dispose(); $path.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
Copy-Item 'D:\code\nekobridge\design\Theme.xaml' 'D:\code\nekobridge\src\NekoBridge.Windows\Theme.xaml'
Copy-Item 'D:\code\nekobridge\design\MainWindow.xaml' 'D:\code\nekobridge\src\NekoBridge.Windows\MainWindow.xaml'
Write-Output 'Applied approved design assets and XAML.'
