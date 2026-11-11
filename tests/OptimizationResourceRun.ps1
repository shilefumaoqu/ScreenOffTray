#requires -Version 7
param([Parameter(Mandatory)][string]$BuildDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('settings','combined')][string]$Mode='combined',[ValidateRange(20,7200)][int]$Seconds=7200,[ValidateRange(0,21)][int]$Cycles=2)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$BuildDirectory=[IO.Path]::GetFullPath($BuildDirectory);$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Fresh output directory required'}
$binary=Join-Path $BuildDirectory 'ScreenOffMemoryIsolation.exe'
$proof=Get-Content -LiteralPath (Join-Path $BuildDirectory 'self-test.json') -Raw|ConvertFrom-Json
if(!$proof.Passed -or $proof.BinaryHash -ne (Get-FileHash -LiteralPath $binary).Hash){throw 'Exact build isolation self-test required'}
$definition=[regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MemoryIsolationRun.ps1') -Raw),"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$definition){throw 'Missing owned-desktop launcher'}
Add-Type -TypeDefinition $definition
Add-Type -Path @((Join-Path $PSScriptRoot 'OwnedProcessIdentity.cs'),(Join-Path $PSScriptRoot 'ExternalResourceCounter.cs'))
[ResourceBudget]::SelfTest();$budget=[ResourceBudget]::new()
[void][IO.Directory]::CreateDirectory($OutputDirectory)
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'source-manifest.json') -Destination $OutputDirectory
$owner=[MemoryPrivateDesktop]::new();$counter=[ExternalResourceCounter]::new();$process=$null;$writer=$null;$diagnostic=$null
$children=@{};$finishedCpu=0.0;$stopReason='';$samples=0;$clock=[Diagnostics.Stopwatch]::StartNew()
$status=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');Mode=$Mode;Seconds=$Seconds;Cycles=$Cycles;Status='running';Binary=$binary;BinaryHash=(Get-FileHash -LiteralPath $binary).Hash;UiHash=(Get-FileHash -LiteralPath (Join-Path $BuildDirectory 'ui/ScreenOffTray.Settings.exe')).Hash;ExternalSampling=$true;FormalMainHooksApply=$false}
function Write-Status {$status|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $OutputDirectory 'status.json') -Encoding utf8}
function Write-Sample($role,$p,$value,$live){
 $writer.WriteLine(($clock.Elapsed.TotalSeconds.ToString('F3',[Globalization.CultureInfo]::InvariantCulture)+','+$role+','+$p.Id+','+$value.PrivateResident+','+$value.WorkingSet+','+$value.PrivateCommit+','+$value.CpuMs.ToString([Globalization.CultureInfo]::InvariantCulture)+','+$value.Handles+','+$value.Threads+','+$value.Gdi+','+$value.User+','+$value.ReadBytes+','+$value.WriteBytes+','+$live.Phase+','+$live.AudioReads+','+$live.DisplayReads+','+$live.DisplayFailures+','+$live.AudioKnown+','+$live.Sources+','+$live.Managed+','+$live.Gc0+','+$live.Gc1+','+$live.Gc2))
}
try {
 $writer=[IO.StreamWriter]::new((Join-Path $OutputDirectory 'resources.csv'),$false,[Text.UTF8Encoding]::new($true));$writer.AutoFlush=$true
 $writer.WriteLine('seconds,role,pid,privateResident,workingSet,privateCommit,cpuMs,handles,threads,gdi,user,readBytes,writeBytes,phase,audioReads,displayReads,displayFailures,audioKnown,sources,managed,gc0,gc1,gc2')
 $diagnostic=[IO.StreamWriter]::new((Join-Path $OutputDirectory 'supervisor.csv'),$false,[Text.UTF8Encoding]::new($true));$diagnostic.AutoFlush=$true;$diagnostic.WriteLine('seconds,privateCommit,workingSet,cpuMs')
 $process=$owner.Start($binary,$OutputDirectory,$Mode,$Seconds,$Cycles);$ticks=$process.StartTime.ToUniversalTime().Ticks
 $status.Pid=$process.Id;$status.StartTicks=$ticks;Write-Status
 $nextSample=1.0;$nextRead=1.0;$lastUi=-1
 while(!$process.WaitForExit(200)) {
  $elapsed=$clock.Elapsed.TotalSeconds
  if($elapsed -gt ($Seconds+30) -and !$stopReason){$stopReason='supervisor-deadline';[IO.File]::WriteAllText((Join-Path $OutputDirectory 'stop.flag'),$stopReason)}
  if($elapsed -gt ($Seconds+50)){throw 'Owned diagnostic did not stop'}
  if($elapsed -lt $nextRead){continue};$nextRead=$elapsed+1
  $livePath=Join-Path $OutputDirectory 'live.json';if(!(Test-Path -LiteralPath $livePath)){continue}
  try{$readStream=[IO.FileStream]::new($livePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$reader=[IO.StreamReader]::new($readStream);try{$live=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}}catch{continue}
  if($elapsed -lt $nextSample -and $lastUi -eq $live.UiPid){continue};$nextSample=$elapsed+10;$lastUi=$live.UiPid
  $sumResident=0L;$sumWorking=0L;$sumCommit=0L;$sumCpu=$finishedCpu
  $v=$counter.Read($process,$ticks,$binary);Write-Sample 'backend' $process $v $live
  $sumResident+=$v.PrivateResident;$sumWorking+=$v.WorkingSet;$sumCommit+=$v.PrivateCommit;$sumCpu+=$v.CpuMs
  if($live.UiPid -gt 0 -and !$children.ContainsKey([string]$live.UiPid)){
   try{$child=[Diagnostics.Process]::GetProcessById($live.UiPid);if($child.StartTime.ToUniversalTime().Ticks -ne $live.UiTicks){$child.Dispose();throw 'UI identity changed'};$children[[string]$child.Id]=[pscustomobject]@{Process=$child;Ticks=$live.UiTicks;Path=$live.UiPath;Cpu=0.0}}catch{if($child){$child.Dispose()}}
  }
  foreach($key in @($children.Keys)){
   $entry=$children[$key];$child=$entry.Process
   if($child.HasExited){$final=[Math]::Max($entry.Cpu,$child.TotalProcessorTime.TotalMilliseconds);$finishedCpu+=$final;$sumCpu+=$final;$child.Dispose();$children.Remove($key);continue}
   try{$cv=$counter.Read($child,$entry.Ticks,$entry.Path)}catch{if($child.HasExited){continue}else{throw}}
   $entry.Cpu=$cv.CpuMs;Write-Sample 'settings' $child $cv $live
   $sumResident+=$cv.PrivateResident;$sumWorking+=$cv.WorkingSet;$sumCommit+=$cv.PrivateCommit;$sumCpu+=$cv.CpuMs
  }
  $samples++;$status.Samples=$samples;$status.ElapsedSeconds=$elapsed;$status.Opened=$live.Opened;$status.Closed=$live.Closed;$status.LastPrivateResident=$sumResident;$status.LastCommit=$sumCommit
  $protection=$budget.Check($sumCommit,$sumWorking,$elapsed,$sumCpu,[Environment]::ProcessorCount);if($protection){$stopReason=$protection}
  if($stopReason){[IO.File]::WriteAllText((Join-Path $OutputDirectory 'stop.flag'),$stopReason)}
  if($stopReason -and !$status.Contains('StopAt')){$status.StopAt=$elapsed};if($status.Contains('StopAt') -and $elapsed-$status.StopAt -gt 20){throw 'Owned test did not stop after protection'}
  $self=[Diagnostics.Process]::GetCurrentProcess();try{$self.Refresh();$diagnostic.WriteLine("$elapsed,$($self.PrivateMemorySize64),$($self.WorkingSet64),$($self.TotalProcessorTime.TotalMilliseconds)")}finally{$self.Dispose()}
  Write-Status
 }
 $status.ExitCode=$owner.ExitCode;$status.Status=if($stopReason){'stopped'}elseif($owner.ExitCode -eq 0){'completed'}else{'failed'};$status.StopReason=$stopReason;$status.ElapsedSeconds=$clock.Elapsed.TotalSeconds
 if(Test-Path -LiteralPath (Join-Path $OutputDirectory 'result.json')){$status.Result=Get-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Raw|ConvertFrom-Json}
 if(!$status.Result){$status.Status='failed';$status.StopReason='missing-result'}
 if($status.Result -and ($status.Result.Status -ne 'completed' -or $status.Result.Seconds -lt $Seconds -or $status.Result.Closed -ne $Cycles -or $status.Result.Shown -ne $Cycles)){$status.Status='stopped';$status.StopReason='requested-or-incomplete'}
 Write-Status
 if($status.Status -ne 'completed'){throw ('Resource run not passed: '+$status.Status+'/'+$stopReason)}
}catch{if($status.Status -ne 'stopped'){$status.Status='failed'};$status.Error=$_.Exception.ToString();Write-Status;[IO.File]::WriteAllText((Join-Path $OutputDirectory 'stop.flag'),'supervisor-failure');throw}
finally {
 if($process){$process.Dispose()};foreach($entry in $children.Values){$entry.Process.Dispose()};$counter.Dispose();$owner.Dispose();$status.ForcedCleanup=$owner.Forced;Write-Status
 if($writer){$writer.Dispose()};if($diagnostic){$diagnostic.Dispose()}
}
