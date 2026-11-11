#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$RunDirectory=[IO.Path]::GetFullPath($RunDirectory)
$results=foreach($directory in Get-ChildItem -LiteralPath $RunDirectory -Directory){
 $path=Join-Path $directory.FullName 'samples.csv';if(!(Test-Path -LiteralPath $path)){continue}
 $stats=@{Count=0;Eligible=0;OverTarget=0;MaxResident=0L;MaxWorking=0L;MaxCommit=0L;MinResident=[long]::MaxValue;First=$null;Last=$null;EligibleFirst=$null;EligibleLast=$null;Histogram=@{}}
 Import-Csv -LiteralPath $path | ForEach-Object {
  $row=$_;$stats.Count++
  if(!$stats.First){$stats.First=$row};$stats.Last=$row
  $resident=[long]$row.privateResident;$working=[long]$row.workingSet;$commit=[long]$row.privateCommit
  $stats.MaxResident=[Math]::Max($stats.MaxResident,$resident);$stats.MaxWorking=[Math]::Max($stats.MaxWorking,$working);$stats.MaxCommit=[Math]::Max($stats.MaxCommit,$commit)
  if($row.phase -eq 'background'){
   if(!$stats.EligibleFirst){$stats.EligibleFirst=$row};$stats.EligibleLast=$row;$stats.Eligible++
   if($resident -ge 20000000){$stats.OverTarget++}
   $stats.MinResident=[Math]::Min($stats.MinResident,$resident)
   if(!$stats.Histogram.ContainsKey($resident)){$stats.Histogram[$resident]=0};$stats.Histogram[$resident]++
  }
 }
 $p50=$null;$p95=$null;$p99=$null;$seen=0
 foreach($key in $stats.Histogram.Keys | Sort-Object){
  $seen+=$stats.Histogram[$key]
  if($null -eq $p50 -and $seen -ge $stats.Eligible*0.50){$p50=$key}
  if($null -eq $p95 -and $seen -ge $stats.Eligible*0.95){$p95=$key}
  if($null -eq $p99 -and $seen -ge $stats.Eligible*0.99){$p99=$key}
 }
 $seconds=if($stats.Last){[double]$stats.Last.seconds-[double]$stats.First.seconds}else{0}
 $reasonPath=Join-Path $directory.FullName 'end-reason.txt'
 [pscustomobject]@{
  Group=$directory.Name;Samples=$stats.Count;SpanSeconds=$seconds;EligibleSamples=$stats.Eligible;SamplesAtOrOver20MB=$stats.OverTarget
  ObservedResidentMinimum=if($stats.Eligible){$stats.MinResident}else{$null};ResidentP50=$p50;ResidentP95=$p95;ResidentP99=$p99
  PeakResident=$stats.MaxResident;PeakWorking=$stats.MaxWorking;PeakCommit=$stats.MaxCommit
  FirstCommit=if($stats.First){[long]$stats.First.privateCommit}else{$null};LastCommit=if($stats.Last){[long]$stats.Last.privateCommit}else{$null}
  EligibleCommitChange=if($stats.Eligible){[long]$stats.EligibleLast.privateCommit-[long]$stats.EligibleFirst.privateCommit}else{$null}
  CpuPercent=if($seconds -gt 0){100*([double]$stats.Last.cpuMs-[double]$stats.First.cpuMs)/($seconds*1000*[Environment]::ProcessorCount)}else{$null}
  EndReason=if(Test-Path $reasonPath){[IO.File]::ReadAllText($reasonPath)}else{'still-running-or-failed'}
  AudioReads=if($stats.Last){[long]$stats.Last.audioReads}else{0};AudioUnknowns=if($stats.Last){[long]$stats.Last.audioUnknowns}else{0}
  MediaReads=if($stats.Last){[long]$stats.Last.mediaReads}else{0};MediaUnknowns=if($stats.Last){[long]$stats.Last.mediaUnknowns}else{0}
  DisplayReads=if($stats.Last){[long]$stats.Last.displayReads}else{0};DisplayFailures=if($stats.Last){[long]$stats.Last.displayFailures}else{0}
  FinalWeakAlive=if($stats.Last){[int]$stats.Last.weakAlive}else{0};FinalGc2=if($stats.Last){[int]$stats.Last.gc2}else{0}
  Opened=if($stats.Last){[int]$stats.Last.opened}else{0};Closed=if($stats.Last){[int]$stats.Last.closed}else{0}
  WindowCloseFallbacks=if($stats.Last){[int]$stats.Last.releaseFailures}else{0}
  Note='Private desktop; safety-guarded diagnostic copy; includes internal counter/sampling overhead. No input hooks or guardian. Never constitutes formal product acceptance. GC estimate is not live-object or committed-heap size.'
 }
}
$results|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $RunDirectory 'summary.json') -Encoding UTF8
$results|Format-Table Group,Samples,EligibleSamples,ResidentP95,PeakCommit,EligibleCommitChange,DisplayFailures,EndReason -AutoSize

# The original campaign is already running with its script body loaded. This
# completion hook honors a later user request without restarting its samples or
# adding another resident watcher. It only signals after the component runner
# has finished and has released its test children.
$campaignDirectory=Split-Path -Parent $RunDirectory
$componentsOnly=Join-Path $campaignDirectory 'components-only.flag'
$phaseStatusPath=Join-Path $RunDirectory 'status.json'
if((Split-Path -Leaf $RunDirectory) -eq 'components' -and
   (Test-Path -LiteralPath $componentsOnly) -and
   (Test-Path -LiteralPath (Join-Path $campaignDirectory 'campaign.json')) -and
   (Test-Path -LiteralPath $phaseStatusPath)){
 $phaseStatus=Get-Content -LiteralPath $phaseStatusPath -Raw|ConvertFrom-Json
 if($phaseStatus.Stage -eq 'components' -and $phaseStatus.Status -eq 'completed'){
  [IO.File]::WriteAllText((Join-Path $campaignDirectory 'stop.flag'),'User requested component tests only; do not start soak.')
  [pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('o');ComponentStatus='completed';Soak='cancelled-by-user';MemoryTargetVerified=$false}|
   ConvertTo-Json|Set-Content -LiteralPath (Join-Path $campaignDirectory 'components-only-completion.json') -Encoding UTF8
 }
}
