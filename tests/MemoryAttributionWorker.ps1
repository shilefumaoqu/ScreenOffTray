# Test-only worker, launched with an external 30-second deadline.
param([Parameter(Mandatory)][string]$IdentityFile,[Parameter(Mandatory)][string]$OutputFile,[switch]$InjectTimeout)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$batch=Join-Path $root 'checks\memory-attribution-20261110'
$OutputFile=[IO.Path]::GetFullPath($OutputFile)
if(!$OutputFile.StartsWith($batch+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Statistics must stay inside this diagnostic batch'}
$identity=Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
$expected=[IO.Path]::GetFullPath($identity.Binary)
if(!$expected.StartsWith($batch+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only this isolated batch can be inspected'}
if((Get-FileHash -LiteralPath $expected).Hash -ne $identity.BinaryHash){throw 'Diagnostic binary identity changed'}
if([IO.Path]::GetFileName($expected) -ne 'MemoryAttributionFixture.exe') {
    $proof=Get-Content -LiteralPath (Join-Path $batch 'self-test.json') -Raw|ConvertFrom-Json
    if(!$proof.Passed){throw 'Passing calibration required'}
    foreach($file in @('MemoryAttributionAnalyzer.cs','MemoryAttributionWorker.ps1','MemoryAttribution.ps1','MemoryAttributionFixture.cs')){
        if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file)).Hash -ne $proof.SourceHashes.$file){throw 'Calibration is stale for current diagnostic sources'}
    }
}
$lib=Join-Path $batch 'tool\Microsoft.Diagnostics.Runtime.dll'
if((Get-FileHash -LiteralPath $lib).Hash -ne '45D5F81732EFCAC02C445343453A3010B1FA3930B13BE5BA16AC620555C37E6C'){throw 'ClrMD pin mismatch'}
[void][Reflection.Assembly]::LoadFrom($lib)
$refs=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)+@($lib)
Add-Type -Path (Join-Path $PSScriptRoot 'MemoryAttributionAnalyzer.cs') -ReferencedAssemblies $refs
$started=if($identity.StartedUtc -is [DateTime]){$identity.StartedUtc}else{[DateTime]::Parse($identity.StartedUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)}
$ticks=$started.ToUniversalTime().Ticks
$result=[MemoryAttributionAnalyzer]::Analyze($identity.Pid,$ticks,$expected,(Join-Path (Split-Path -Parent $IdentityFile) 'native-allocations.json'),$InjectTimeout.IsPresent)
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputFile -Encoding UTF8
if($InjectTimeout){if(!$result.ResourcesReleased -or !$result.SameIdentityAfter){exit 2};exit 0}
if($result.Status -ne 'complete'){exit 1}
