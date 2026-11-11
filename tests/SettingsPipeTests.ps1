$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root ('checks/optimization-01911/pipe-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output|Out-Null
$exe=Join-Path $output 'SettingsPipeTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /platform:x64 /target:exe /langversion:5 /codepage:65001 ('/out:'+$exe) /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll (Join-Path $root 'WpfSettings/SettingsClient.cs') (Join-Path $root 'SettingsWire.cs') (Join-Path $root 'SettingsPipeHost.cs') (Join-Path $PSScriptRoot 'SettingsPipeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Compile failed'}
& $exe 2>&1 | Tee-Object (Join-Path $output 'results.txt')
if($LASTEXITCODE -ne 0){throw 'Pipe tests failed'}
