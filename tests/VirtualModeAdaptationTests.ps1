param([string]$OutputDirectory='', [switch]$AtomicFileOnly, [switch]$ReviewFixesOnly, [switch]$BusyNotificationOnly, [switch]$ReviewP2Only, [switch]$RouteSelectionOnly)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory) { $OutputDirectory=Join-Path $projectRoot 'checks/mode-adaptation-20261107/isolated' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
[xml]$project=Get-Content -LiteralPath (Join-Path $projectRoot 'ScreenOffTray.csproj') -Raw
$sources=@($project.SelectNodes("//*[local-name()='Compile']") | ForEach-Object {Join-Path $projectRoot $_.Include}) + @(Join-Path $PSScriptRoot 'VirtualModeAdaptationTests.cs')
$references=@($project.SelectNodes("//*[local-name()='Reference']") | ForEach-Object {'/r:'+$_.Include+'.dll'})
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary=Join-Path $OutputDirectory 'VirtualModeAdaptationTests.exe'
$log=& $compiler /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /main:ScreenOffTray.Tests.VirtualModeAdaptationTests ('/out:'+$binary) $references $sources 2>&1
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'compile.txt'),@($log | ForEach-Object {$_.ToString()}),[Text.Encoding]::UTF8)
if($LASTEXITCODE -ne 0) { throw "Compilation failed: $log" }
[string[]]$testArguments=@()
if($AtomicFileOnly) { $testArguments=@('--atomic-file-only') }
if($ReviewFixesOnly) { $testArguments=@('--review-fixes-only') }
if($BusyNotificationOnly) { $testArguments=@('--busy-notification-only') }
if($ReviewP2Only) { $testArguments=@('--review-p2-only') }
if($RouteSelectionOnly) { $testArguments=@('--route-selection-only') }
& $binary @testArguments | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if($LASTEXITCODE -ne 0) {throw 'Mode adaptation checks failed.'}
