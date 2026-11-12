param([ValidateRange(1024,65535)][int]$Port=24870)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'NekoBridge.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请将此脚本放在 NekoBridge.exe 旁运行。' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请在管理员 PowerShell 中运行此脚本。只会允许专用网络和本地子网。' }
foreach ($protocol in @('TCP','UDP')) {
    $name = "NekoBridge-$protocol-$Port"
    $existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if ($existing) { throw "规则 $name 已存在；请先检查现有配置，不自动覆盖。" }
}
foreach ($protocol in @('TCP','UDP')) {
    New-NetFirewallRule -Name "NekoBridge-$protocol-$Port" -DisplayName "NekoBridge LAN $protocol $Port" -Direction Inbound -Action Allow -Program $exe -Protocol $protocol -LocalPort $Port -RemoteAddress LocalSubnet -Profile Private | Out-Null
}
Write-Output '已允许 NekoBridge 在专用网络的本地子网通信；未开放公网或关闭防火墙。'
