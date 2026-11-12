$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$dll = Join-Path $root 'src\NekoBridge.Windows\bin\Release\net10.0-windows\NekoBridge.dll'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
$exe = Join-Path $root 'src\NekoBridge.Windows\bin\Release\net10.0-windows\NekoBridge.exe'
$process = Start-Process -FilePath $exe -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw ('应用提前退出：' + $process.ExitCode) }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw '应用没有创建窗口。' }
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $process.Id
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if (-not $window) { throw '找不到真实应用窗口。' }
    Write-Output ('Window: ' + $window.Current.Name)
    $find = {
        param($id)
        $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $id
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
    }
    $pause = & $find 'PauseButton'
    if (-not $pause) { throw '未加载主界面，找不到暂停按钮。' }
    $invoke = $pause.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do { $paused = $pause.Current.Name; if ($paused -eq '恢复共享') { break }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $deadline)
    if ($paused -ne '恢复共享') { throw ('暂停交互失败：' + $paused) }
    $invoke.Invoke()
    Write-Output 'PASS: real WPF pause/resume interaction'
    $status = & $find 'StatusText'
    if ($status.Current.Name -like '*失败*') { throw $status.Current.Name }
    Write-Output ('Status: ' + $status.Current.Name)
    $tabs = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty),([System.Windows.Automation.ControlType]::TabItem)))
    foreach ($tab in $tabs) {
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Write-Output ('PASS: tab ' + $tab.Current.Name)
    }
    if (-not (& $find 'SaveSettingsButton')) { throw '设置界面未加载。' }
    Write-Output 'PASS: actual desktop application loaded and interactive; no screenshots or visual comparison performed'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
