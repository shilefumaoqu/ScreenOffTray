$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root ('checks/optimization-01911/ui-lifetime-final-'+(Get-Date -Format yyyyMMdd-HHmmss));[void][IO.Directory]::CreateDirectory($out)
$binary=Join-Path $out 'WpfStartupLifetimeFixture.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /target:exe /platform:x64 /langversion:5 ('/out:'+$binary) /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll (Join-Path $root 'SettingsWire.cs') (Join-Path $root 'SettingsPipeHost.cs') (Join-Path $PSScriptRoot 'WpfStartupLifetimeFixture.cs')
if($LASTEXITCODE){throw 'Fixture compile failed'}
$definition=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'MemoryIsolationRun.ps1') -Raw),"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
Add-Type -TypeDefinition $definition
$env:SCREENOFF_LIFETIME_TEST_UI=Join-Path $root 'WpfSettings/bin/Release/ScreenOffTray.Settings.exe'
$results=[Collections.Generic.List[string]]::new()
foreach($mode in @('invalid','before-connect','error-dialog','connected')){
 $directory=Join-Path $out $mode;[void][IO.Directory]::CreateDirectory($directory);$owner=[MemoryPrivateDesktop]::new();$parent=$null;$child=$null
 try {
  $parent=$owner.Start($binary,$directory,$mode,1,1);$watch=[Diagnostics.Stopwatch]::StartNew();$identity=Join-Path $directory 'ui-identity.txt'
  while(!(Test-Path $identity) -and !$parent.HasExited -and $watch.ElapsedMilliseconds -lt 5000){Start-Sleep -Milliseconds 20}
  if(!(Test-Path $identity)){throw 'No own UI identity'}
  $lines=[IO.File]::ReadAllLines($identity);if($lines.Length -ne 3){throw 'Incomplete identity receipt'}
  try{$child=[Diagnostics.Process]::GetProcessById([int]$lines[0]);if($child.StartTime.ToUniversalTime().Ticks -ne [long]$lines[1]){throw 'UI identity changed'};$handle=$child.Handle}catch [ArgumentException]{$child=$null}
  if(!$parent.WaitForExit($(if($mode -eq "error-dialog"){15000}else{10000})) -or $owner.ExitCode -ne 0){throw 'Fake backend failed'}
  if($child -and !$child.WaitForExit(8000)){throw 'UI outlived its backend'}
  $results.Add('PASS '+$mode+': exact own UI exits without manual input when backend ends')
 }finally{if($child){$child.Dispose()};if($parent){$parent.Dispose()};$owner.Dispose()}
}
$results.Add('Private non-input desktops; real UI and fake backend only; no formal Main, hooks, displays, configuration or process control')
$results|Set-Content (Join-Path $out 'results.txt') -Encoding utf8;$results
