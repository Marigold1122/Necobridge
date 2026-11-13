param([Parameter(Mandatory=$true)][string]$Source)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assets=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\NekoBridge.Windows\Assets'
Copy-Item -LiteralPath $Source -Destination (Join-Path $assets 'logo.png')
$image=[Drawing.Image]::FromFile($Source)
$frames=@();$sizes=@(16,20,24,32,48,64,128,256)
try{
 foreach($size in $sizes){
  $bitmap=New-Object Drawing.Bitmap $size,$size
  $g=[Drawing.Graphics]::FromImage($bitmap);$memory=New-Object IO.MemoryStream
  try{
   $g.Clear([Drawing.Color]::Transparent);$g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $scale=[Math]::Min($size/[double]$image.Width,$size/[double]$image.Height)
   $w=[single]($image.Width*$scale);$h=[single]($image.Height*$scale)
   $g.DrawImage($image,[single](($size-$w)/2),[single](($size-$h)/2),$w,$h)
   $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png);$frames+=,$memory.ToArray()
  }finally{$memory.Dispose();$g.Dispose();$bitmap.Dispose()}
 }
 $writer=New-Object IO.BinaryWriter ([IO.File]::Create((Join-Path $assets 'nekobridge.ico')))
 try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i -lt $sizes.Count;$i++){
   $d=if($sizes[$i]-eq 256){0}else{$sizes[$i]}
   $writer.Write([byte]$d);$writer.Write([byte]$d);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
   $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset);$offset+=$frames[$i].Length
  }
  foreach($frame in $frames){$writer.Write([byte[]]$frame)}
 }finally{$writer.Dispose()}
}finally{$image.Dispose()}
