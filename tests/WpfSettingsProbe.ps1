param([int]$Runs=11,[string]$OutputDirectory="",[string]$Language="zh-CN",[switch]$LayoutTests,[switch]$ResizeDiagnostic,[switch]$CompileOnly,[switch]$PipeFixture,[switch]$StageTimings,[switch]$ProcessSoftware,[string]$UiBinary="",[ValidateRange(0,10)][int]$PauseBetweenRunsSeconds=0)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=if($OutputDirectory){$OutputDirectory}else{Join-Path $root ('checks/optimization-01911/wpf-probe-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$referenceRoot=Join-Path $env:LOCALAPPDATA 'ScreenOffTray-Build/Tools/net48-1.0.3/build/'
$refs=@('/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.dll','/r:System.Core.dll',('/r:'+(Join-Path $framework 'WPF/PresentationFramework.dll')),('/r:'+(Join-Path $framework 'WPF/PresentationCore.dll')),('/r:'+(Join-Path $framework 'WPF/WindowsBase.dll')),('/r:'+(Join-Path $framework 'System.Xaml.dll')))
if(!(Test-Path -LiteralPath (Join-Path $framework 'WPF/WindowsBase.dll'))){$refs=$refs|ForEach-Object{$_ -replace 'WPF/WindowsBase.dll','WindowsBase.dll'}}
& (Join-Path $framework 'MSBuild.exe') (Join-Path $root 'WpfSettings/ScreenOffTray.Settings.csproj') /p:Configuration=Release ('/p:TargetFrameworkRootPath='+$referenceRoot) /v:quiet /nologo
if($LASTEXITCODE){throw 'Compiled WPF markup build failed'}
$resources=@('/resource:'+ (Join-Path $root 'WpfSettings/obj/Release/ScreenOffTray.Settings.g.resources')+',WpfSettingsProbe.g.resources')
$generatedCode=Join-Path $output 'SettingsWindow.probe.g.cs'
$markupCode=[IO.File]::ReadAllText((Join-Path $root 'WpfSettings/obj/Release/SettingsWindow.g.cs'))
$markupCode=$markupCode.Replace('/ScreenOffTray.Settings;component/','/WpfSettingsProbe;component/')
[IO.File]::WriteAllText($generatedCode,$markupCode,[Text.UTF8Encoding]::new($true))
if($PipeFixture){$refs+='/r:System.Xml.dll';$fixtureSources=@((Join-Path $root 'WpfSettings/Program.cs'),(Join-Path $root 'WpfSettings/SettingsClient.cs'),(Join-Path $root 'SettingsPipeHost.cs'));$fixtureMain='/main:WpfPipeFixture'}else{$fixtureSources=@();$fixtureMain='/main:'+$(if($LayoutTests){'WpfSettingsLayoutTests'}elseif($ResizeDiagnostic){'WpfResizeDiagnostic'}else{'WpfSettingsProbe'})}
if($StageTimings){
 if(!$PipeFixture){throw 'Stage timing requires the real UI pipe fixture'}
 $program=[IO.File]::ReadAllText((Join-Path $root 'WpfSettings/Program.cs'))
 $trace='private static readonly System.Diagnostics.Stopwatch traceClock=System.Diagnostics.Stopwatch.StartNew(); private static void Mark(string stage){System.IO.File.AppendAllText(System.IO.Path.Combine(Environment.GetEnvironmentVariable("SCREENOFF_WPF_FIXTURE_OUTPUT"),"startup-stages.csv"),stage+","+traceClock.Elapsed.TotalMilliseconds+Environment.NewLine);}'
 $program=$program.Replace('private static Application application;',$trace+' private static Application application;')
 if($ProcessSoftware){$program=$program.Replace('application=new Application','System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;application=new Application')}
 $program=$program.Replace('var artwork=SettingsWindow.PreloadArtwork();','Mark("entry");var artwork=SettingsWindow.PreloadArtwork();Mark("preload-started");')
 $program=$program.Replace('client=new SettingsClient(args[1],parent,ticks);','Mark("application-ready");client=new SettingsClient(args[1],parent,ticks);Mark("pipe-connected");')
 $program=$program.Replace('artwork.GetAwaiter().GetResult();Build(null);','artwork.GetAwaiter().GetResult();Mark("art-ready");Build(null);Mark("window-shown");')
 $program=$program.Replace('var next=new SettingsWindow(language=="en");','Mark("view-construct-start");var next=new SettingsWindow(language=="en");Mark("view-constructed");')
 $program=$program.Replace('next.ContentRendered+=delegate {if(window==next)','next.ContentRendered+=delegate {Mark("content-rendered");if(window==next)')
 $program=$program.Replace('state=packet.State;','state=packet.State;Mark("state-received");')
 $generated=Join-Path $output 'Program.stage.cs';[IO.File]::WriteAllText($generated,$program,[Text.UTF8Encoding]::new($true));$fixtureSources[0]=$generated
}
$binary=Join-Path $output 'WpfSettingsProbe.exe'
& (Join-Path $framework 'csc.exe') /nologo /utf8output /target:exe /platform:x64 /langversion:5 /optimize+ /codepage:65001 $fixtureMain ('/out:'+$binary) ('/win32manifest:'+(Join-Path $root 'WpfSettings/app.manifest')) $refs $resources $fixtureSources $generatedCode (Join-Path $root 'WpfSettings/SettingsWindow.cs') (Join-Path $root 'WpfSettings/WindowSizingPolicy.cs') (Join-Path $root 'WpfSettings/ApplicationIcons.cs') (Join-Path $root 'Ui/ApplicationIconCache.cs') (Join-Path $root 'Playback/AudioSourceNames.cs') (Join-Path $root 'HotkeyBinding.cs') (Join-Path $root 'SettingsWire.cs') (Join-Path $PSScriptRoot $(if($PipeFixture){'WpfPipeFixture.cs'}elseif($LayoutTests){'WpfSettingsLayoutTests.cs'}elseif($ResizeDiagnostic){'WpfResizeDiagnostic.cs'}else{'WpfSettingsProbe.cs'})) 2>&1|Tee-Object (Join-Path $output 'compile.txt')
if($LASTEXITCODE -ne 0){throw 'WPF compile failed'}
Copy-Item -LiteralPath (Join-Path $root 'WpfSettings/App.config') -Destination ($binary+'.config')
if($CompileOnly){Write-Output $binary;return}
$launcher=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MemoryIsolationRun.ps1') -Raw
$definition=[regex]::Match($launcher,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$definition){throw 'Existing private-desktop supervisor not found'}
Add-Type -TypeDefinition $definition
if($UiBinary){$UiBinary=[IO.Path]::GetFullPath($UiBinary);if(!$PipeFixture -or !$UiBinary.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($UiBinary) -ne 'ScreenOffTray.Settings.exe'){throw 'Benchmark only this workspace UI binary'};[Environment]::SetEnvironmentVariable('SCREENOFF_UI_BENCH_BINARY',$UiBinary)}else{[Environment]::SetEnvironmentVariable('SCREENOFF_UI_BENCH_BINARY',$null)}
$rows=@()
for($i=0;$i -lt $Runs;$i++){
 if($i -gt 0 -and $PauseBetweenRunsSeconds){Start-Sleep -Seconds $PauseBetweenRunsSeconds}
 $run=Join-Path $output ('run-'+$i);New-Item -ItemType Directory -Path $run -Force|Out-Null
 $ready=Join-Path $run 'ready-ms.txt';if(Test-Path -LiteralPath $ready){throw 'Run evidence already exists; use a fresh output directory'}
 $clock=[Diagnostics.Stopwatch]::StartNew();$owner=New-Object MemoryPrivateDesktop; $process=$owner.Start($binary,$run,$Language,1,1)
 try {
 $identity=$process.StartTime.ToUniversalTime().Ticks
 while(!(Test-Path -LiteralPath $ready) -and !$process.HasExited -and $clock.ElapsedMilliseconds -lt $(if($LayoutTests -or $ResizeDiagnostic){120000}else{15000})){Start-Sleep -Milliseconds 10}
 $wall=$clock.Elapsed.TotalMilliseconds
 if(!(Test-Path -LiteralPath $ready)) {throw ('WPF window did not become interactive. Owned PID='+$process.Id+'; start='+$identity)}
 if(!$process.WaitForExit(10000)){throw 'Probe did not close normally; investigate owned process'}
 if($owner.ExitCode -ne 0){throw 'Probe failed'}
 $rows+=[pscustomobject]@{Run=$i;ParentToReadyMs=$wall;ChildToReadyMs=[double](Get-Content -LiteralPath $ready);ExitCode=$owner.ExitCode}
 } finally {if($process){$process.Dispose()};$owner.Dispose()}
}
$rows|Export-Csv -LiteralPath (Join-Path $output 'timings.csv') -NoTypeInformation -Encoding UTF8
if($LayoutTests -or $ResizeDiagnostic){
 [pscustomobject]@{Suite=$(if($LayoutTests){'layout'}else{'resize-diagnostic'});Passed=$true;ElapsedMs=$rows[0].ParentToReadyMs;Scope='whole isolated suite duration, NOT opening time; see per-test evidence'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding UTF8
 Get-Content -LiteralPath (Join-Path $output 'summary.json');return
}
# The pipe fixture launches a fresh fake backend as well as the UI. The
# user-visible Settings action begins in the already-running backend, so gate
# on its stopwatch before Host.Start, not on creating that extra fake backend.
$metric=if($PipeFixture){'ChildToReadyMs'}else{'ParentToReadyMs'}
$warm=@($rows|Select-Object -Skip 1|Sort-Object $metric)
$p95=if($warm.Count){$warm[[Math]::Ceiling($warm.Count*.95)-1].$metric}else{$null}
$outer=@($rows|Select-Object -Skip 1|Sort-Object ParentToReadyMs)
$outerP95=if($outer.Count){$outer[[Math]::Ceiling($outer.Count*.95)-1].ParentToReadyMs}else{$null}
[pscustomobject]@{ColdMs=$rows[0].$metric;WarmP95Ms=$p95;ColdPass=($rows[0].$metric -le 2000);WarmPass=$(if($Runs -gt 1){$p95 -le 1000}else{$null});Metric=$metric;PauseBetweenRunsSeconds=$PauseBetweenRunsSeconds;OuterFreshBackendColdMs=$rows[0].ParentToReadyMs;OuterFreshBackendWarmP95Ms=$outerP95;Scope='fresh UI processes, private desktop and fake owner; pipe gate measures Host.Start to first state plus ContentRendered; outer includes extra fake backend startup; no DWM frame metric'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $output 'summary.json')
