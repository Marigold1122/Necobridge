$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw '缺少工程本地 .NET 10 SDK，请先完成环境安装。' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& $dotnet build (Join-Path $root 'src\NekoBridge.Windows\NekoBridge.Windows.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
