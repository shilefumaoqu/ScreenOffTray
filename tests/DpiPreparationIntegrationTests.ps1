param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'checks/release-01913/preparation-integration'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'ScreenOffTray.csproj') -Raw
$sources=@($project.SelectNodes("//*[local-name()='Compile']")|ForEach-Object{Join-Path $root $_.Include})+@(Join-Path $PSScriptRoot 'DpiPreparationIntegrationTests.cs')
$refs=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$exe=Join-Path $OutputDirectory 'DpiPreparationIntegrationTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /main:ScreenOffTray.Tests.DpiPreparationIntegrationTests ('/out:'+$exe) $refs $sources *> (Join-Path $OutputDirectory 'compile.txt')
if($LASTEXITCODE){Get-Content -LiteralPath (Join-Path $OutputDirectory 'compile.txt');throw 'Preparation integration compile failed'}
& $exe|Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if($LASTEXITCODE){throw 'Preparation integration tests failed'}
