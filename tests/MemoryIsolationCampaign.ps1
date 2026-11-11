#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$IncludeSoak)
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$BuildDirectory=[IO.Path]::GetFullPath($BuildDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a fresh campaign directory'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
if(!$IncludeSoak){[IO.File]::WriteAllText((Join-Path $OutputDirectory 'components-only.flag'),'Component tests only; 24h soak requires an explicit separate choice.')}
$status=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');Status='running';Stage='components';Components=$null;Soak=$null;Error=$null}
$lock=$null
function Save-Campaign {$status|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputDirectory 'campaign.json') -Encoding UTF8}
try {
 $lock=[IO.File]::Open((Join-Path $OutputDirectory 'owned.lock'),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 Save-Campaign
 & (Join-Path $PSScriptRoot 'MemoryIsolationRun.ps1') -BuildDirectory $BuildDirectory -OutputDirectory (Join-Path $OutputDirectory 'components') -Stage components -AdditionalStopFile (Join-Path $OutputDirectory 'stop.flag')
 & (Join-Path $PSScriptRoot 'MemoryIsolationSummary.ps1') -RunDirectory (Join-Path $OutputDirectory 'components')
 $components=Get-Content -LiteralPath (Join-Path $OutputDirectory 'components/status.json') -Raw|ConvertFrom-Json
 $status.Components=$components.Status
 if($components.Status -ne 'completed'){throw 'Component tests incomplete; no soak started'}
 $summaries=Get-Content -LiteralPath (Join-Path $OutputDirectory 'components/summary.json') -Raw|ConvertFrom-Json
 foreach($row in $summaries){
  if($row.Group -in @('audio','media','playback') -and $row.AudioReads -le $row.AudioUnknowns){throw "No valid audio reads in $($row.Group); no soak started"}
  if($row.Group -in @('media','playback') -and $row.MediaReads -le $row.MediaUnknowns){throw "No valid media reads in $($row.Group); no soak started"}
  if($row.Group -eq 'display' -and $row.DisplayReads -le $row.DisplayFailures){throw 'No valid display queries; no soak started'}
  if($row.Group -eq 'settings' -and ($row.Closed -lt 21 -or $row.WindowCloseFallbacks -ne 0)){throw 'Incomplete normal settings lifecycle; no soak started'}
 }
 if(Test-Path -LiteralPath (Join-Path $OutputDirectory 'components-only.flag')){$status.Status='completed-components-awaiting-analysis';return}
 if(Test-Path -LiteralPath (Join-Path $OutputDirectory 'stop.flag')){$status.Status='stopped';return}
 $status.Stage='soak';Save-Campaign
 & (Join-Path $PSScriptRoot 'MemoryIsolationRun.ps1') -BuildDirectory $BuildDirectory -OutputDirectory (Join-Path $OutputDirectory 'soak') -Stage soak -AdditionalStopFile (Join-Path $OutputDirectory 'stop.flag')
 & (Join-Path $PSScriptRoot 'MemoryIsolationSummary.ps1') -RunDirectory (Join-Path $OutputDirectory 'soak')
 $soak=Get-Content -LiteralPath (Join-Path $OutputDirectory 'soak/status.json') -Raw|ConvertFrom-Json
 $status.Soak=$soak.Status;$status.Status=if($soak.Status -eq 'completed'){'completed-awaiting-analysis'}else{'incomplete'}
}catch{$status.Status='incomplete';$status.Error=$_.Exception.ToString();throw}
finally{if($lock){$lock.Dispose()};$status.EndedUtc=[DateTime]::UtcNow.ToString('o');Save-Campaign}
