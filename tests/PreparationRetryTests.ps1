param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory) { $OutputDirectory=Join-Path $projectRoot 'checks/repair-01912/preparation' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
[xml]$project=Get-Content -LiteralPath (Join-Path $projectRoot 'ScreenOffTray.csproj') -Raw
$sources=@($project.SelectNodes("//*[local-name()='Compile']") | ForEach-Object {Join-Path $projectRoot $_.Include}) + @(Join-Path $PSScriptRoot 'PreparationRetryTests.cs')
$references=@($project.SelectNodes("//*[local-name()='Reference']") | ForEach-Object {'/r:'+$_.Include+'.dll'})
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary=Join-Path $OutputDirectory 'PreparationRetryTests.exe'
# Compile the same internal state and pure decision helper, but select this test
# as the sole entry point. Fake evidence/platforms never invoke the product Main.
$log=& $compiler /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /main:ScreenOffTray.Tests.PreparationRetryTests ('/out:'+$binary) $references $sources 2>&1
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'compile.txt'),@($log | ForEach-Object {$_.ToString()}),[Text.Encoding]::UTF8)
if($LASTEXITCODE -ne 0) { throw "Compilation failed: $log" }
& $binary | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if($LASTEXITCODE -ne 0) {throw 'Setup guidance checks failed.'}
