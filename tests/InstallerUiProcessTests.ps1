$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root ('checks/optimization-01911/installer-process-'+(Get-Date -Format yyyyMMdd-HHmmss))
[void][IO.Directory]::CreateDirectory($out)
$source=Join-Path $out 'Fixture.cs';$binary=Join-Path $out 'ScreenOffTray.Settings.exe';$helper=Join-Path $root 'Installer/bin/Release/ScreenOffTray.DriverSetup.exe'
@'
using System;using System.IO;using System.Threading;
internal static class Fixture{private static int Main(){string directory=AppDomain.CurrentDomain.BaseDirectory;File.WriteAllText(Path.Combine(directory,"ready"),"ready");for(int i=0;i<600 && !File.Exists(Path.Combine(directory,"stop"));i++)Thread.Sleep(50);return 0;}}
'@|Set-Content -LiteralPath $source -Encoding utf8
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /target:exe /platform:x64 ('/out:'+$binary) $source
if($LASTEXITCODE){throw 'Fake process compile failed'}
$results=[Collections.Generic.List[string]]::new()
$empty=& $helper --verify-processes --app-dir $out|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $empty.status -ne 'verified-stopped'){throw 'Stopped path refused'};$results.Add('PASS exact stopped directory, read-only process verification')
$child=Start-Process -FilePath $binary -WindowStyle Hidden -PassThru
try {
 $clock=[Diagnostics.Stopwatch]::StartNew();while(!(Test-Path -LiteralPath (Join-Path $out 'ready')) -and $clock.ElapsedMilliseconds -lt 5000){Start-Sleep -Milliseconds 50}
 if(!(Test-Path -LiteralPath (Join-Path $out 'ready'))){throw 'Owned fake UI did not initialize'}
 $denied=& $helper --verify-processes --app-dir $out|ConvertFrom-Json
 if($LASTEXITCODE -eq 0 -or $denied.status -ne 'failed'){throw 'Running exact UI was not refused'};$results.Add('PASS running exact settings executable prevents overwrite')
 $other=& $helper --verify-processes --app-dir (Join-Path $out 'different-installation')|ConvertFrom-Json
 if($LASTEXITCODE -ne 0 -or $other.status -ne 'verified-stopped'){throw 'Unrelated directory was treated as owned'};$results.Add('PASS different-directory UI is not stopped or claimed')
}finally{[IO.File]::WriteAllText((Join-Path $out 'stop'),'stop');if(!$child.WaitForExit(5000)){throw 'Owned fake process did not exit normally'};$child.Dispose()}
$after=& $helper --verify-processes --app-dir $out|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $after.status -ne 'verified-stopped'){throw 'Post-exit verification failed'};$results.Add('PASS normal fake exit permits overwrite')
$results.Add('BOUNDARY process-only helper branch; no driver enumeration, store writes, install/uninstall or formal process control')
$results|Set-Content -LiteralPath (Join-Path $out 'results.txt') -Encoding utf8
$results
