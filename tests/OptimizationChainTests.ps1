param([switch]$Baseline,[switch]$View)
if($Baseline -and $View){throw "View tests use candidate only"}
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$source=if($Baseline){Join-Path $root 'checks/optimization-01911/baseline'}else{$root}
$output=Join-Path $root $(if($View){'checks/optimization-01911/runtime-view'}else{'checks/optimization-01911/chain-'+$(if($Baseline){'baseline'}else{'candidate'})})
New-Item -ItemType Directory -Path $output -Force|Out-Null
[xml]$project=Get-Content -LiteralPath (Join-Path $source 'ScreenOffTray.csproj') -Raw
$files=@($project.SelectNodes("//*[local-name()='Compile']")|ForEach-Object{Join-Path $source $_.Include})
if($Baseline){$files+=Join-Path $root 'RuntimeCoordination.cs'}
$files+=Join-Path $PSScriptRoot $(if($View){'RuntimeViewTests.cs'}else{'OptimizationChainTests.cs'})
$refs=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$binary=Join-Path $output 'OptimizationChainTests.exe'
$opts=@('/nologo','/target:exe','/platform:x64','/langversion:5','/codepage:65001',('/main:ScreenOffTray.Tests.'+$(if($View){'RuntimeViewTests'}else{'OptimizationChainTests'})),('/out:'+$binary))
if($Baseline){$opts+='/define:BASELINE'}
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" $opts $refs $files 2>&1|Tee-Object (Join-Path $output 'compile.txt')
if($LASTEXITCODE -ne 0){throw 'Compile failed'}
& $binary 2>&1|Tee-Object (Join-Path $output 'results.txt')
if($LASTEXITCODE -ne 0){throw 'Focused tests failed'}
