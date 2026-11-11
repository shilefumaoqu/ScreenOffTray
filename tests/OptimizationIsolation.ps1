#requires -Version 7.0
param([string]$OutputDirectory='', [switch]$CompileOnly)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'checks/optimization-01911/resource-build'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$copies=Join-Path $OutputDirectory 'guarded-source'
[void][IO.Directory]::CreateDirectory($copies)
$utf8=[Text.UTF8Encoding]::new($true)
$manifest=[Collections.Generic.List[object]]::new()
function Replace-Exact([string]$text,[string]$old,[string]$new){
 if(!$text.Contains($old)){throw "Diagnostic guard did not match: $old"}
 return $text.Replace($old,$new)
}
function Deny-Body([string]$text,[string]$signature){
 $at=$text.IndexOf($signature,[StringComparison]::Ordinal)
 if($at -lt 0){throw "Missing guarded method $signature"}
 $begin=$text.IndexOf('{',$at); $level=1; $i=$begin+1
 # C# lexical scan: braces in strings/comments do not terminate the body.
 $state='code'
 for(;$i -lt $text.Length;$i++){
  $c=$text[$i];$n=if($i+1 -lt $text.Length){$text[$i+1]}else{[char]0}
  if($state -eq 'line'){if($c -eq "`n"){$state='code'};continue}
  if($state -eq 'block'){if($c -eq '*' -and $n -eq '/'){$state='code';$i++};continue}
  if($state -eq 'verbatim'){if($c -eq '"'){if($n -eq '"'){$i++}else{$state='code'}};continue}
  if($state -eq 'string' -or $state -eq 'char'){
   if($c -eq '\'){$i++;continue}
   if(($state -eq 'string' -and $c -eq '"') -or ($state -eq 'char' -and $c -eq "'")){$state='code'};continue
  }
  if($c -eq '/' -and $n -eq '/'){$state='line';$i++;continue}
  if($c -eq '/' -and $n -eq '*'){$state='block';$i++;continue}
  if($c -eq '@' -and $n -eq '"'){$state='verbatim';$i++;continue}
  if($c -eq '"'){$state='string';continue}; if($c -eq "'"){$state='char';continue}
  if($c -eq '{'){$level++};if($c -eq '}'){$level--;if($level -eq 0){break}}
 }
 if($level -ne 0){throw 'Guard lexical scan failed'}
 return $text.Substring(0,$begin)+'{ throw new InvalidOperationException("Blocked in isolated memory diagnostics"); }'+$text.Substring($i+1)
}
[xml]$project=[IO.File]::ReadAllText((Join-Path $root 'ScreenOffTray.csproj'))
$sources=foreach($node in $project.SelectNodes("//*[local-name()='Compile']")){
 $relative=$node.Include;$original=Join-Path $root $relative
 $text=[IO.File]::ReadAllText($original);$before=$text
 if($relative -eq 'Program.cs'){$text=Deny-Body $text 'private static void Main(string[] args)'}
 if($relative -eq 'TrayApplicationContext.cs'){$text=Deny-Body $text 'internal TrayApplicationContext(bool initialSetupRequested = false,Action<bool,bool> startupConfirmation=null)'}
 if($relative -eq 'Properties\Settings.cs'){
  $text=Replace-Exact $text 'get { return DefaultInstance; }' 'get { throw new System.InvalidOperationException("Formal settings blocked in memory diagnostics"); }'
 }
 if($relative -eq 'Oled\RuntimeBridge.cs'){
  $text=Replace-Exact $text 'Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)' 'AppDomain.CurrentDomain.BaseDirectory'
  $text=Replace-Exact $text '"ScreenOffTray", "运行记录"' '"diagnostic-runtime", "logs"'
  $text=Replace-Exact $text 'internal static class Native' 'internal static class Native'
  $text=$text.Replace('internal delegate IntPtr HookProc','public const bool MemoryIsolationGuard = true; internal delegate IntPtr HookProc')
  foreach($name in @('SetWindowsHookEx','RegisterHotKey','SetThreadExecutionState','SetDisplayConfig')){
   $pattern='(?s)\[DllImport\([^\]]+\)\]\s*internal static extern (?<ret>\w+) '+$name+'\((?<args>[^;]+)\);'
   $match=[regex]::Match($text,$pattern)
   if(!$match.Success){throw "Missing native guard $name"}
   $replacement='internal static '+$match.Groups['ret'].Value+' '+$name+'('+$match.Groups['args'].Value+') { throw new InvalidOperationException("Blocked native operation in diagnostic"); }'
   $text=$text.Remove($match.Index,$match.Length).Insert($match.Index,$replacement)
  }
 }
 if($relative -eq 'Oled\DisplayBackend.cs'){
  foreach($signature in @('public void TurnOff()','public void Restore()','public static void Recover(string path)','public void SaveRecovery(string path)')){$text=Deny-Body $text $signature}
  $text=Replace-Exact $text 'private static class Native' 'internal static class Native'
  $needle='internal static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,'
  $text=Replace-Exact $text $needle 'internal static extern int ValidateDisplayConfigNative(uint pathCount, [In] PathInfo[] paths,'
  # Preserve the native entry name while making the managed boundary validate-only.
  $native='[DllImport("user32.dll", ExactSpelling = true)]'+"`r`n"+'            internal static extern int ValidateDisplayConfigNative'
  if(!$text.Contains($native)){$native=$native.Replace("`r`n","`n")}
  $text=Replace-Exact $text $native ($native.Replace('ExactSpelling = true','EntryPoint = "SetDisplayConfig", ExactSpelling = true'))
  $insert='internal static class Native'+"`n        {`n"+'            internal static int SetDisplayConfig(uint pc, PathInfo[] p, uint mc, ModeInfo[] m, uint flags) { if ((flags & 0x80U) != 0 || (flags & 0x40U) == 0 || (flags & ~(0x20U|0x40U|0x400U|0x8000U)) != 0) throw new InvalidOperationException("Only VALIDATE permitted"); return ValidateDisplayConfigNative(pc,p,mc,m,flags); }'
  $text=[regex]::Replace($text,'internal static class Native\s*\{',[Text.RegularExpressions.MatchEvaluator]{param($m) $insert},1)
 }
 if($relative -eq 'DisplaySetup.cs'){
  $text=Deny-Body $text 'internal bool PrepareDisplay(Func<IVirtualModePlatform> platform)'
  $text=Deny-Body $text 'private async Task<bool> PrepareDisplayAsync(Func<IVirtualModePlatform> platform)'
  $text=Deny-Body $text 'private bool AutoPrepare(VirtualModeRequirement requirement, Action verifyMode)'
  # This class's native write import is unnecessary for Probe(false). Deny all calls.
  $needle='[DllImport("user32.dll", ExactSpelling = true)] private static extern int SetDisplayConfig(uint pc, [In] DisplayBackend.PathInfo[] paths, uint mc, [In] DisplayBackend.ModeInfo[] modes, uint flags);'
  $text=Replace-Exact $text $needle 'private static int SetDisplayConfig(uint pc, DisplayBackend.PathInfo[] paths, uint mc, DisplayBackend.ModeInfo[] modes, uint flags) { throw new InvalidOperationException("Display apply blocked in diagnostics"); }'
 }
 if($relative -eq 'VirtualModeNative.cs'){
  $text=Deny-Body $text 'public string Commit(byte[] original, byte[] desired, VirtualModeRequirement requirement)'
  $text=Deny-Body $text 'public void Restart(VirtualModeRequirement requirement)'
 }
 $destination=Join-Path $copies $relative;[void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
 [IO.File]::WriteAllText($destination,$text,$utf8)
 $manifest.Add([pscustomobject]@{Path=$relative;SourceHash=(Get-FileHash -LiteralPath $original).Hash;DiagnosticHash=(Get-FileHash -LiteralPath $destination).Hash;Modified=($before -cne $text)})
 $destination
}
$manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-manifest.json') -Encoding UTF8
$sources+=Join-Path $PSScriptRoot 'OptimizationIsolationProbe.cs'
$sources+=Join-Path $PSScriptRoot 'OwnedProcessIdentity.cs'
$references=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$resources=@($project.SelectNodes("//*[local-name()='EmbeddedResource']")|ForEach-Object{'/resource:'+(Join-Path $root $_.Include)+','+$_.LogicalName})
$binary=Join-Path $OutputDirectory 'ScreenOffMemoryIsolation.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$log=& $compiler /nologo /utf8output /target:exe /platform:x64 /optimize+ /langversion:5 /codepage:65001 /main:ScreenOffTray.Tests.OptimizationIsolationProbe ('/out:'+$binary) $references ('/win32manifest:'+(Join-Path $root 'app.manifest')) $resources $sources 2>&1
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'compile.txt'),[string[]]@($log|ForEach-Object{$_.ToString()}),$utf8)
if($LASTEXITCODE){throw "Diagnostic build failed: $log"}
if(!$CompileOnly){
 & $binary --self-test | Tee-Object -FilePath (Join-Path $OutputDirectory 'self-test.txt');if($LASTEXITCODE){throw 'Isolation self-test failed'}
 [pscustomobject]@{BinaryHash=(Get-FileHash -LiteralPath $binary).Hash;Passed=$true;Utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'self-test.json') -Encoding UTF8
}
Write-Output $binary
