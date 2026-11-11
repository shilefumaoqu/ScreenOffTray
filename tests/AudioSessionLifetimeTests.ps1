#requires -Version 7.0
param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'checks/memory-isolation-20261109/audio-product-unit'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[xml]$project=[IO.File]::ReadAllText((Join-Path $root 'ScreenOffTray.csproj'))
$sources=@($project.SelectNodes("//*[local-name()='Compile']")|ForEach-Object{Join-Path $root $_.Include})+@(Join-Path $PSScriptRoot 'AudioSessionLifetimeTests.cs')
$refs=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$exe=Join-Path $OutputDirectory 'AudioSessionLifetimeTests.exe'
$log=& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /optimize+ /platform:x64 /langversion:5 /main:ScreenOffTray.Tests.AudioSessionLifetimeTests ('/out:'+$exe) $refs $sources 2>&1
$log|Set-Content (Join-Path $OutputDirectory 'compile.txt') -Encoding UTF8
if($LASTEXITCODE){throw ('Fake contract compilation failed: '+($log -join "`n"))}
& $exe 2>&1|Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if($LASTEXITCODE){throw 'Audio lifetime fake-contract tests failed'}
