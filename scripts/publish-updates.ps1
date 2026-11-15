$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$vpk = Join-Path $root '.tools\vpk\vpk.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
[xml]$project = Get-Content (Join-Path $root 'src\NekoBridge.Windows\NekoBridge.Windows.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$output = Join-Path $root "artifacts\NekoBridge-$version-updates"
$publish = Join-Path $root ".tools\publish-$version"
if (Test-Path $output) { throw 'Update output exists; choose a new version instead of overwriting.' }
if (-not (Test-Path $vpk)) { throw 'Install matching vpk: dotnet tool install vpk --version 1.2.161 --tool-path .tools/vpk' }
& $dotnet publish (Join-Path $root 'src\NekoBridge.Windows\NekoBridge.Windows.csproj') -c Release -r win-x64 --self-contained true -o $publish -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item (Join-Path $root 'docs\使用说明.md') $publish
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish
Copy-Item (Join-Path $root 'scripts\允许局域网连接.ps1') $publish
$bundle = Join-Path $root ".tools\velopack-$version"
$wizard = Join-Path $root ".tools\setup-$version"
if (Test-Path $bundle) { throw 'Native bundle directory exists; inspect it before rebuilding.' }
& $vpk pack --packId NekoBridge --packVersion $version --packDir $publish --mainExe NekoBridge.exe --packTitle NekoBridge --channel win --runtime win-x64 --icon (Join-Path $root 'src\NekoBridge.Windows\Assets\nekobridge.ico') --outputDir $bundle --delta None
if ($LASTEXITCODE -ne 0) { throw 'Velopack pack failed.' }
$payload = Join-Path $bundle 'NekoBridge-win-Setup.exe'
& $dotnet publish (Join-Path $root 'src\NekoBridge.Setup\NekoBridge.Setup.csproj') -c Release -r win-x64 --self-contained true -o $wizard -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:SetupPayload=$payload" "-p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw 'Installation wizard publish failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
Get-ChildItem -LiteralPath $bundle -File | Where-Object { $_.Name -ne 'NekoBridge-win-Setup.exe' } | Copy-Item -Destination $output
Copy-Item (Join-Path $wizard 'NekoBridge-Setup.exe') (Join-Path $output 'NekoBridge-win-Setup.exe')
Write-Output "Update artifacts: $output"
Write-Output 'Release must include wizard Setup.exe, Portable.zip, full.nupkg and releases.win.json; the native payload stays private.'
