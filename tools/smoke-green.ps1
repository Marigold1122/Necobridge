$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT=Join-Path $root '.tools\dotnet'
$env:PATH=$env:DOTNET_ROOT+';'+$env:PATH
$exe=Join-Path $root 'src\NekoBridge.Windows\bin\Release\net10.0-windows\NekoBridge.exe'
$process=Start-Process -FilePath $exe -PassThru
try{
 $deadline=[DateTime]::UtcNow.AddSeconds(20)
 do{$process.Refresh();if($process.HasExited){throw 'Application exited before window ready'};if($process.MainWindowHandle -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 if(-not $window){throw 'Application window missing'}
 $find={param($id) $c=New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id; $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)}
 foreach($id in @('NavConn','NavArr','NavSet')){
  $nav=& $find $id;if(-not $nav){throw "Missing navigation $id"}
  $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Write-Output "PASS real navigation $id"
 }
 $pause=& $find 'PauseToggle';if(-not $pause){throw 'Missing pause control'}
 $toggle=$pause.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
 $initial=$toggle.Current.ToggleState;$toggle.Toggle()
 $deadline=[DateTime]::UtcNow.AddSeconds(5)
 do{if($toggle.Current.ToggleState -ne $initial){break};Start-Sleep -Milliseconds 50}while([DateTime]::UtcNow -lt $deadline)
 if($toggle.Current.ToggleState -eq $initial){throw 'Pause toggle failed'}
 $toggle.Toggle();Write-Output 'PASS pause and resume'
 $nav=& $find 'NavConn';$nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $pair=& $find 'CardAction'
 if(-not $pair -or -not $pair.Current.IsEnabled){throw 'Pairing must be available, not a disabled preview'}
 $invoke=$pair.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
 $before=$pair.Current.Name;$invoke.Invoke()
 $deadline=[DateTime]::UtcNow.AddSeconds(5)
 do{if($pair.Current.Name -ne $before){break};Start-Sleep -Milliseconds 50}while([DateTime]::UtcNow -lt $deadline)
 if($pair.Current.Name -eq $before){throw 'Pairing state did not change'}
 $invoke.Invoke();Write-Output 'PASS real pairing enable/disable (secret not logged)'
 Write-Output 'PASS three-entry working app smoke'
}finally{if(-not $process.HasExited){Stop-Process -Id $process.Id}}
