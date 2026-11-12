$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT=Join-Path $root '.tools\dotnet'
$p=Start-Process (Join-Path $root 'src\NekoBridge.Windows\bin\Release\net10.0-windows\NekoBridge.exe') -PassThru
try{
 $end=[DateTime]::UtcNow.AddSeconds(20)
 do{$p.Refresh();if($p.HasExited){throw 'App exited'};if($p.MainWindowHandle -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end)
 $w=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
 $find={param($scope,$id)$c=New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id;$scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)}
 $nav=& $find $w 'NavSet';$nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $button=& $find $w 'HistorySettingsButton';$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $name=New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty),'NekoBridge · 剪贴板'
 $end=[DateTime]::UtcNow.AddSeconds(10)
 do{$history=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$name);if($history){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end)
 if(-not $history){throw 'History window missing'}
 $recorder=& $find $history 'HotkeyRecorder';if(-not $recorder){throw 'Hotkey settings missing'}
 if($recorder.Current.Name -ne 'Alt+V'){throw 'Default shortcut differs'}
 Write-Output 'PASS real history window and default Alt+V settings'
 $all=$history.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 if($all.Count -lt 20){throw 'History UI failed to load'}
 Write-Output 'PASS actual WPF history controls rendered without invoking clipboard'
}
finally{if(-not $p.HasExited){Stop-Process -Id $p.Id}}
