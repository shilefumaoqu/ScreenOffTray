param([string]$OutputDirectory = '',[switch]$Probe,[switch]$RebuildProbe,[switch]$Remaining,[switch]$LayoutConfirmation,[switch]$TimeoutProbe,[string]$Group = '',[string[]]$Groups = @(),[switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'checks/c-ui-0.1.9-20261027/ui' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary = Join-Path $OutputDirectory 'CSettingsUiHarness.exe'
# Explicit UI/pure-data allowlist; excludes product entry, coordinator, settings,
# audio monitors and every display/device adapter. No visible window is shown.
$sources = @('Ui/SettingsDialog.cs','Ui/PlaybackSettingsPanel.cs','Ui/MintControls.cs','Ui/SettingsDpiContext.cs','Ui/AnimatedCatControl.cs','Ui/ApplicationIconCache.cs','Ui/TrayMenu.cs','HotkeyBinding.cs','AutoOffTiming.cs','Playback/PlaybackContracts.cs','tests/CSettingsUiTests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
$resources = @('cat-actions.png','cat-variety.png') | ForEach-Object { '/resource:' + (Join-Path $projectRoot ('Ui/Resources/' + $_)) + ',ScreenOffTray.Ui.Resources.' + $_ }
$log = & $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /langversion:5 /warn:4 /main:ScreenOffTray.Tests.CSettingsUiTests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$binary" $resources $sources 2>&1
$code = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'ui-compile.txt'), (($log -join [Environment]::NewLine) + [Environment]::NewLine + "csc exit=$code"), [Text.Encoding]::UTF8)
if ($code -ne 0) { throw "Isolated C UI compilation failed: $log" }
if ($CompileOnly) { return }
$entries = if ($Groups.Count -gt 0) { @($Groups | ForEach-Object { '--group=' + $_ }) } elseif ($TimeoutProbe) { @('--timeout-probe') } elseif ($Probe) { @('--probe') } elseif ($RebuildProbe) { @('--rebuild-probe') } elseif ($Group) { @('--group=' + $Group) } elseif ($Remaining) {
    @('--group=dpi','--group=modeless','--group=render-zh-CN-144','--group=render-en-96','--group=render-en-120','--group=render-en-144','--group=render-en-192')
} elseif ($LayoutConfirmation) {
    @('--group=render-zh-CN-144','--group=render-en-96','--group=render-en-120','--group=render-en-144','--group=render-en-192')
} else {
    @('--group=contexts','--group=drafts','--group=lifetime','--group=preparation','--group=integration','--group=dpi','--group=modeless','--group=render-zh-CN-96','--group=render-zh-CN-120','--group=render-zh-CN-144','--group=render-zh-CN-192','--group=render-en-96','--group=render-en-120','--group=render-en-144','--group=render-en-192')
}
$summary = @()
$failed = $false
foreach ($entry in $entries) {
    $name = $entry.Replace('--group=','').Replace('--','')
    $caseOutput = if ($Probe -or $RebuildProbe -or $TimeoutProbe) { $OutputDirectory } else { Join-Path $OutputDirectory $name }
    New-Item -ItemType Directory -Path $caseOutput -Force | Out-Null
    & $binary $caseOutput $entry
    $code = $LASTEXITCODE
    $summary += "$name exit=$code"
    if ($code -ne 0) { $failed = $true }
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'ui-groups.txt'), $summary, [Text.Encoding]::UTF8)
    Write-Output "$name exit=$code"
}
if ($failed) { throw 'C UI checks failed; see each bounded group result, progress and DrawToBitmap captures.' }
