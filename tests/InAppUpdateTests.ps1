param([string]$OutputDirectory='checks/in-app-update/tests',[switch]$Live)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force|Out-Null
$projectDirectory=Join-Path $root 'src/ScreenOffTray'
[xml]$project=Get-Content -LiteralPath (Join-Path $projectDirectory 'ScreenOffTray.csproj') -Raw
$files=@($project.SelectNodes("//*[local-name()='Compile']")|ForEach-Object{Join-Path $projectDirectory $_.Include})
$files+=Join-Path $PSScriptRoot 'InAppUpdateTests.cs'
$refs=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$binary=Join-Path $output 'InAppUpdateTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /target:exe /platform:x64 /langversion:5 /codepage:65001 /main:ScreenOffTray.Tests.InAppUpdateTests ('/out:'+$binary) $refs $files
if($LASTEXITCODE){throw 'Compile failed'}
Copy-Item -LiteralPath (Join-Path $projectDirectory 'App.config') -Destination ($binary+'.config')
$arguments=@((Join-Path $output 'temporary-files'));if($Live){$arguments+='--live'}
& $binary $arguments 2>&1 | Tee-Object (Join-Path $output 'results.txt')
if($LASTEXITCODE){throw 'In-app update tests failed'}
$installer=Get-Content -LiteralPath (Join-Path $root 'Installer/ScreenOffTray.iss') -Raw
if($installer -notmatch "(?s)param:UPDATEONLY\|0.*?not IsUpgrade.*?ResumeFirstInstall.*?RollbackOnly.*?Result := False.*?Exit;"){throw 'Missing update-only installation guard'}
'PASS installer update-only guard rejects fresh install, pending driver continuation and rollback (source check; not a real installation)'
if(!$Live){
 $controller=Join-Path $output 'UpdateControllerTests.exe'
 $sources=@('src/ScreenOffTray/TrayUpdates.cs','src/ScreenOffTray/UpdateDownload.cs','src/ScreenOffTray/UpdateChecker.cs','src/Shared/SettingsWire.cs','tests/UpdateControllerTests.cs')|ForEach-Object{Join-Path $root $_}
 & "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /target:exe /platform:x64 /langversion:5 /codepage:65001 ('/out:'+$controller) /r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll $sources
 if($LASTEXITCODE){throw 'Controller compile failed'}
 & $controller 2>&1 | Tee-Object (Join-Path $output 'controller-results.txt')
 if($LASTEXITCODE){throw 'Controller tests failed'}
}
