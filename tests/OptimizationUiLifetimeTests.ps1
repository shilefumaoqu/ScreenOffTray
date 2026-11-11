param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root ('checks/optimization-01911/ui-lifetime-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output -Force|Out-Null
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'ScreenOffTray.csproj') -Raw
$sources=@($project.SelectNodes("//*[local-name()='Compile']")|ForEach-Object{Join-Path $root $_.Include})+@(Join-Path $PSScriptRoot 'OptimizationUiLifetimeTests.cs')
$refs=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$resources=@($project.SelectNodes("//*[local-name()='EmbeddedResource']")|ForEach-Object{'/resource:'+(Join-Path $root $_.Include)+','+$_.LogicalName})
$binary=Join-Path $output 'OptimizationUiLifetimeTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /main:ScreenOffTray.Tests.OptimizationUiLifetimeTests ('/out:'+$binary) $refs $resources $sources 2>&1|Tee-Object (Join-Path $output 'compile.txt')
if($LASTEXITCODE -ne 0){throw 'Compile failed'}
$launcher=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MemoryIsolationRun.ps1') -Raw
$definition=[regex]::Match($launcher,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
Add-Type -TypeDefinition $definition
$owner=New-Object MemoryPrivateDesktop
try {
 $process=$owner.Start($binary,$output,'ui-lifetime',0,0)
 if(!$process.WaitForExit(30000)){throw 'Owned UI lifetime test timed out'}
 Get-Content -LiteralPath (Join-Path $output 'results.txt')
 if($owner.ExitCode -ne 0){throw 'UI lifetime checks failed'}
 $process.Dispose()
}finally{$owner.Dispose()}
