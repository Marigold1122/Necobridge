$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw '缺少工程本地 .NET 10 SDK。' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\NekoBridge.Windows\NekoBridge.Windows.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw '项目未声明版本。' }
$output = Join-Path $root "artifacts\NekoBridge-$version-win-x64"
$zip = Join-Path $root "artifacts\NekoBridge-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { throw '压缩包已存在，请先核对旧产物后另存或移除，避免误覆盖。' }
if (Test-Path -LiteralPath $output) { throw '发布目录已存在，请先核对旧产物后另存或移除，避免保留旧文件。' }
& $dotnet publish (Join-Path $root 'src\NekoBridge.Windows\NekoBridge.Windows.csproj') -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Copy-Item (Join-Path $root 'docs\使用说明.md') (Join-Path $output '使用说明.md')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $output
Copy-Item (Join-Path $root 'scripts\允许局域网连接.ps1') $output
Compress-Archive -Path $output -DestinationPath $zip
Write-Output "可测试程序：$output\NekoBridge.exe"
Write-Output "分发压缩包：$zip"
