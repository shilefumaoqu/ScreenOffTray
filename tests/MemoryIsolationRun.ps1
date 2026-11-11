#requires -Version 7.0
param(
 [Parameter(Mandatory=$true)][string]$BuildDirectory,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [ValidateSet('smoke','components','soak')][string]$Stage='components',
 [ValidateSet('base','audio','media','playback','display','settings','combined','attribution')][string[]]$SmokeGroups,
 [ValidateRange(0,7200)][int]$SecondsOverride=0,
 [ValidateSet('none','after-launch','stop-write')][string]$FaultMode='none',
 [string]$AdditionalStopFile=''
)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public sealed class MemoryPrivateDesktop : IDisposable {
 IntPtr desktop,processHandle;uint ownedPid;long creationTime;string expectedPath;
 public bool Forced {get;private set;}
 public Process Start(string exe,string directory,string group,int seconds,int cycles) {
  string name="ScreenOffMemory-"+Guid.NewGuid().ToString("N");
  desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
  if(desktop==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
  if(exe.Contains("\"")||directory.Contains("\""))throw new ArgumentException("Invalid path");
  var start=new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfo)),Desktop=name }; ProcessInfo info;
  if(!CreateProcess(exe,new StringBuilder("\""+exe+"\" \""+directory+"\" "+group+" "+seconds+" "+cycles),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref start,out info))throw new System.ComponentModel.Win32Exception();
  processHandle=info.Process;ownedPid=info.ProcessId;expectedPath=exe;CloseHandle(info.Thread);
  long a,b,c;if(!GetProcessTimes(processHandle,out creationTime,out a,out b,out c))throw new System.ComponentModel.Win32Exception();
  return Process.GetProcessById((int)info.ProcessId);
 }
 public uint ExitCode {get {uint value;if(!GetExitCodeProcess(processHandle,out value))throw new System.ComponentModel.Win32Exception();return value;}}
 public void Dispose(){
  try {
   uint wait=processHandle==IntPtr.Zero?0:WaitForSingleObject(processHandle,10000);
   if(wait!=0 && wait!=258)throw new InvalidOperationException("Owned process wait failed");
   if(processHandle!=IntPtr.Zero && wait==258) {
    long created,a,b,c;var path=new StringBuilder(32768);int length=path.Capacity;
    if(GetProcessId(processHandle)!=ownedPid || !GetProcessTimes(processHandle,out created,out a,out b,out c) || created!=creationTime ||
       !QueryFullProcessImageName(processHandle,0,path,ref length) || !String.Equals(path.ToString(),expectedPath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refuse termination: owned native handle identity mismatch");
    if(!TerminateProcess(processHandle,3) || WaitForSingleObject(processHandle,5000)!=0)throw new InvalidOperationException("Owned diagnostic process exit not confirmed");Forced=true;
   }
  } finally {
   if(processHandle!=IntPtr.Zero){CloseHandle(processHandle);processHandle=IntPtr.Zero;}
   if(desktop!=IntPtr.Zero){if(!CloseDesktop(desktop))throw new System.ComponentModel.Win32Exception();desktop=IntPtr.Zero;}
  }
 }
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo {internal int Size;internal string Reserved,Desktop,Title;internal int X,Y,Width,Height,XChars,YChars,Fill,Flags;internal short Show,ReservedBytes;internal IntPtr ReservedPointer,Input,Output,Error;}
 [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {internal IntPtr Process,Thread;internal uint ProcessId,ThreadId;}
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
 [DllImport("user32.dll",SetLastError=true)] static extern bool CloseDesktop(IntPtr desktop);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr ps,IntPtr ts,bool inherit,uint flags,IntPtr environment,string cwd,ref StartupInfo start,out ProcessInfo process);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
 [DllImport("kernel32.dll")] static extern uint GetProcessId(IntPtr process);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out long creation,out long exit,out long kernel,out long user);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref int length);
 [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr process,uint milliseconds);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint code);
}
'@
$binary=Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) 'ScreenOffMemoryIsolation.exe'
if(!(Test-Path -LiteralPath $binary)){throw 'Build the guarded diagnostic first'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a fresh run directory; never overwrite a prior sample'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$binaryHash=(Get-FileHash -LiteralPath $binary).Hash
$receipt=Get-Content -LiteralPath (Join-Path $BuildDirectory 'self-test.json') -Raw | ConvertFrom-Json
if(!$receipt.Passed -or $receipt.BinaryHash -ne $binaryHash){throw 'A passing isolation self-test for this exact binary is required'}
[IO.File]::Copy((Join-Path $BuildDirectory 'source-manifest.json'),(Join-Path $OutputDirectory 'source-manifest.json'))
$groups=if($Stage -eq 'soak'){@('combined')}elseif($Stage -eq 'smoke'){@('base','audio','media','playback','display','settings')}else{@('base','audio','media','playback','display','settings')}
if($SmokeGroups){if($Stage -ne 'smoke'){throw 'Group override is for bounded smoke tests only'};$groups=$SmokeGroups}
if($FaultMode -ne 'none' -and $Stage -ne 'smoke'){throw 'Fault injection is for bounded smoke tests only'}
$duration=if($Stage -eq 'soak'){86400}elseif($Stage -eq 'smoke'){25}else{1200}
if($SecondsOverride){$duration=$SecondsOverride}
$cycles=if($Stage -eq 'smoke'){1}else{21}
if($SmokeGroups -contains 'settings' -and $SecondsOverride -gt 1000){$cycles=21}
$results=[Collections.Generic.List[object]]::new()
$manifest=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');Stage=$Stage;Binary=$binary;BinaryHash=$binaryHash;SecondsPerGroup=$duration;Cycles=$cycles;SourceManifestHash=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'source-manifest.json')).Hash;RunnerHash=(Get-FileHash -LiteralPath $PSCommandPath).Hash;Status='running';FormalProcessUntouched=$true;NoSnapshots=$true}
if($groups -contains 'attribution'){$manifest.NoSnapshots=$false;$manifest.SnapshotScope='Externally supervised snapshots of this isolated target only'}
$supervisor=$null
$statusPath=Join-Path $OutputDirectory 'status.json'
function File-StopRequested {return (Test-Path -LiteralPath (Join-Path $OutputDirectory 'stop.flag')) -or ($AdditionalStopFile -and (Test-Path -LiteralPath $AdditionalStopFile))}
function usingSelfSample {
 $self=[Diagnostics.Process]::GetCurrentProcess()
 try{$self.Refresh();$supervisor.WriteLine(('{0},{1},{2},{3},{4},{5},{6}' -f [DateTime]::UtcNow.ToString('o'),$self.Id,$self.PrivateMemorySize64,$self.WorkingSet64,$self.TotalProcessorTime.TotalMilliseconds,$self.HandleCount,$self.Threads.Count))}finally{$self.Dispose()}
}
try {
 $supervisor=[IO.StreamWriter]::new((Join-Path $OutputDirectory 'supervisor.csv'),$false,[Text.UTF8Encoding]::new($true));$supervisor.AutoFlush=$true
 $supervisor.WriteLine('utc,pid,privateCommit,workingSet,cpuMs,handles,threads')
 foreach($group in $groups){
  if(File-StopRequested){$manifest.Status='stopped';break}
  if((Get-FileHash -LiteralPath $binary).Hash -ne $binaryHash){throw 'Diagnostic binary changed during run'}
  $directory=Join-Path $OutputDirectory $group;[void][IO.Directory]::CreateDirectory($directory)
  $manifest.CurrentGroup=$group;$manifest.Results=$results.ToArray();$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $statusPath -Encoding UTF8
  $desktop=[MemoryPrivateDesktop]::new()
  $process=$null
  try {
   $process=$desktop.Start($binary,$directory,$group,$duration,$cycles)
   $start=$process.StartTime.ToUniversalTime();$watch=[Diagnostics.Stopwatch]::StartNew();$timedOut=$false
   $externalNext=0
   [pscustomobject]@{Pid=$process.Id;StartedUtc=$start.ToString('o');Binary=$binary;BinaryHash=$binaryHash}|ConvertTo-Json|Set-Content (Join-Path $directory 'owned-process.json') -Encoding UTF8
   if($FaultMode -ne 'none'){throw "Injected supervisor failure: $FaultMode"}
   while(!$process.WaitForExit(1000)){
    if($group -eq 'attribution' -and $watch.Elapsed.TotalSeconds -ge $externalNext){
     $externalNext=$watch.Elapsed.TotalSeconds+10
     try{$process.Refresh();[pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('o');Threads=$process.Threads.Count;Handles=$process.HandleCount}|ConvertTo-Json -Compress|Add-Content -LiteralPath (Join-Path $directory 'external-counters.jsonl') -Encoding UTF8}catch{if(!$process.HasExited){throw}}
    }
    if(File-StopRequested){[IO.File]::WriteAllText((Join-Path $directory 'stop.flag'),'stop')}
    if($watch.Elapsed.TotalSeconds -gt $duration+45){$timedOut=$true;break}
    if(([int]$watch.Elapsed.TotalSeconds)%60 -eq 0){
     usingSelfSample
    }
   }
   if($timedOut -or !$process.HasExited){
    [IO.File]::WriteAllText((Join-Path $directory 'stop.flag'),'stop')
    if(!$process.WaitForExit(10000)){
     # The common cleanup below verifies and terminates by the original held
     # native creation handle, never by an enumerated PID or process name.
     $timedOut=$true
    }
   }
   $reason=if(Test-Path (Join-Path $directory 'end-reason.txt')){[IO.File]::ReadAllText((Join-Path $directory 'end-reason.txt'))}else{'missing-end-reason'}
   $exitCode=$desktop.ExitCode
   $row=[pscustomobject]@{Group=$group;Seconds=$watch.Elapsed.TotalSeconds;Exit=$exitCode;TimedOut=$timedOut;EndReason=$reason}
   $results.Add($row)
   if($timedOut -or $exitCode -ne 0 -or $reason -ne 'duration'){$manifest.Status='incomplete';break}
  }finally{
   try {
    try {if($FaultMode -eq 'stop-write'){throw 'Injected stop marker write failure'};[IO.File]::WriteAllText((Join-Path $directory 'stop.flag'),'stop')}
    catch {$manifest.StopMarkerError=$_.Exception.Message}
    $desktop.Dispose()
    [pscustomobject]@{Forced=$desktop.Forced;OwnedExitConfirmed=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directory 'supervisor-cleanup.json') -Encoding UTF8
   }finally{if($process){$process.Dispose()}}
  }
 }
 if($manifest.Status -eq 'running'){$manifest.Status='completed'}
}catch{$manifest.Status='failed';$manifest.Error=$_.Exception.ToString();throw}
finally{
 if($supervisor){$supervisor.Dispose()}
 $manifest.EndedUtc=[DateTime]::UtcNow.ToString('o');$manifest.Results=$results.ToArray();$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $statusPath -Encoding UTF8
}

