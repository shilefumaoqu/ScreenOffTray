#requires -Version 7.0
param([string]$IdentityFile='', [string]$OutputFile='', [switch]$SelfTest)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$batch=Join-Path $root 'checks\memory-attribution-20261110'
function Invoke-Analysis([string]$identity,[string]$output,[bool]$inject=$false) {
    $output=[IO.Path]::GetFullPath($output)
    if(!$output.StartsWith($batch+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay inside this diagnostic batch'}
    if(Test-Path -LiteralPath $output){throw 'Use a new evidence filename'}
    $info=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($arg in @('-NoProfile','-File',(Join-Path $PSScriptRoot 'MemoryAttributionWorker.ps1'),'-IdentityFile',$identity,'-OutputFile',$output)){$info.ArgumentList.Add($arg)}
    if($inject){$info.ArgumentList.Add('-InjectTimeout')}
    $worker=[Diagnostics.Process]::Start($info)
    $stdout=$worker.StandardOutput.ReadToEndAsync();$stderr=$worker.StandardError.ReadToEndAsync()
    try {
        $start=$worker.StartTime.ToUniversalTime();$handle=$worker.Handle
        $done=$worker.WaitForExit(30000)
        if(!$done){
            if($worker.StartTime.ToUniversalTime() -ne $start -or $worker.MainModule.FileName -ne $info.FileName){throw 'Owned diagnostic worker identity changed'}
            $worker.Kill();if(!$worker.WaitForExit(5000)){throw 'Diagnostic worker did not exit'}
        }
        [pscustomobject]@{TimedOut=(!$done);WorkerExit=$worker.ExitCode;TargetNeverKilled=$true;TimeoutMilliseconds=30000} | ConvertTo-Json | Set-Content -LiteralPath ($output+'.supervisor.json') -Encoding UTF8
        if(!$done -or $worker.ExitCode -ne 0){throw 'Analysis unavailable, partial or timed out; do not retry this snapshot automatically'}
        Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    } finally {
        try {
            if($worker.HasExited){
                [IO.File]::WriteAllText(($output+'.stdout.txt'),$stdout.GetAwaiter().GetResult())
                [IO.File]::WriteAllText(($output+'.stderr.txt'),$stderr.GetAwaiter().GetResult())
            }
        } finally {$worker.Dispose()}
    }
}
if(!$SelfTest){
    if(!$IdentityFile -or !$OutputFile){throw 'Identity and output required'}
    Invoke-Analysis ([IO.Path]::GetFullPath($IdentityFile)) ([IO.Path]::GetFullPath($OutputFile)) | Select-Object Status,CaptureMs,ElapsedMs,Objects,ReachableObjects,RootedBytes,UnrootedBytes,ResourcesReleased
    return
}
$fixture=Join-Path $batch ('calibration-'+(Get-Date -Format 'HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
[void][IO.Directory]::CreateDirectory($fixture)
$exe=Join-Path $fixture 'MemoryAttributionFixture.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:x64 /langversion:5 ('/out:'+$exe) (Join-Path $PSScriptRoot 'MemoryAttributionFixture.cs')
if($LASTEXITCODE){throw 'Calibration fixture compile failed'}
$held=Start-Process -FilePath $exe -ArgumentList @('"'+$fixture+'"') -WindowStyle Hidden -PassThru
try {
    [pscustomobject]@{Pid=$held.Id;StartedUtc=$held.StartTime.ToUniversalTime().ToString('o');Binary=$exe;BinaryHash=(Get-FileHash -LiteralPath $exe).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'identity.json') -Encoding UTF8
    Start-Sleep -Milliseconds 800
    $result=Invoke-Analysis (Join-Path $fixture 'identity.json') (Join-Path $fixture 'normal.json')
    foreach($expected in @(@('System.Byte[]',3145752),@('System.Char[]',1048600),@('System.Byte[]',524312))){
        if(!($result.LargestRooted | Where-Object {$_.Type -eq $expected[0] -and $_.Bytes -eq $expected[1]})){throw ('Missing calibrated rooted object '+$expected[0]+':'+$expected[1])}
    }
    if($result.LargestRooted | Where-Object {$_.Type -eq 'System.Byte[]' -and $_.Bytes -eq 1048600}){throw 'Weak-only array incorrectly marked strong'}
    $native=@($result.LiveMemoryMap.Groups | Where-Object Label -eq 'calibration-native-buffer')
    if($native.Count -ne 1 -or $native[0].Bytes -lt (2097152-8192)){throw 'Native page classification did not match known buffer'}
    if($result.LiveMemoryMap.PrivateResident -ne $result.LiveMemoryMap.ClassifiedTotal){throw 'Resident accounting mismatch'}
    # Static-only types may lack a DAC MethodTable. Missing optional owner names
    # are kept unresolved; do not invent a source from type/size resemblance.
    $timeout=Invoke-Analysis (Join-Path $fixture 'identity.json') (Join-Path $fixture 'injected-timeout.json') $true
    if(!$timeout.Error.Contains('Injected after-capture timeout') -or !$timeout.ResourcesReleased -or !$timeout.SameIdentityAfter){throw 'Timeout cleanup validation failed'}
    $heartbeat=Get-Content -LiteralPath (Join-Path $fixture 'heartbeat.txt') -Raw
    Start-Sleep -Milliseconds 400
    if((Get-Content -LiteralPath (Join-Path $fixture 'heartbeat.txt') -Raw) -eq $heartbeat){throw 'Target did not resume heartbeat'}
    $hashes=[ordered]@{};foreach($file in @('MemoryAttributionAnalyzer.cs','MemoryAttributionWorker.ps1','MemoryAttribution.ps1','MemoryAttributionFixture.cs')){$hashes[$file]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file)).Hash}
    [pscustomobject]@{Passed=$true;RootedArrays=$true;DependentHandle=$true;WeakOnlyExcluded=$true;NativePagesReconciled=$true;TimeoutReleased=$true;HeartbeatResumed=$true;SourceHashes=$hashes;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $batch 'self-test.json')
} finally {
    [IO.File]::WriteAllText((Join-Path $fixture 'stop.flag'),'stop')
    if(!$held.WaitForExit(5000)){throw 'Calibration target did not stop normally'}
    $held.Dispose()
}
