param([string]$OutputDirectory = '', [switch]$Baseline, [switch]$CompileOnly, [switch]$Initial)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'checks/ui-details-20261108/confirm' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary = Join-Path $OutputDirectory 'UiDetailHarness.exe'
# UI and pure contracts only. All persistence/device callbacks are fake.
$sources = @('Ui/SettingsDialog.cs','Ui/PlaybackSettingsPanel.cs','Ui/MintControls.cs','Ui/SettingsDpiContext.cs','Ui/StaticCatControl.cs','Ui/ApplicationIconCache.cs','Ui/TrayMenu.cs','HotkeyBinding.cs','AutoOffTiming.cs','Playback/PlaybackContracts.cs','tests/CSettingsUiTests.cs','tests/SettingsRefreshUiTests.cs','tests/UiDetailTests.cs') | ForEach-Object { Join-Path $root $_ }
$resources = @('header-cat.png','header-scene.png','card-left-scene.png','card-right-scene.png','footer-flowers.png') | ForEach-Object { '/resource:' + (Join-Path $root ('Ui/Resources/theme/' + $_)) + ',ScreenOffTray.Ui.Resources.theme.' + $_ }
$log = & $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /langversion:5 /warn:4 /main:ScreenOffTray.Tests.CSettingsUiTests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$binary" $resources $sources 2>&1
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'compile.txt'), ($log -join [Environment]::NewLine))
if ($LASTEXITCODE) { throw "UI-only compile failed: $log" }
if ($CompileOnly) { return }
$group = if ($Initial) { '--group=ui-details-initial' } elseif ($Baseline) { '--group=ui-details-baseline' } else { '--group=ui-details-confirm' }
& $binary $OutputDirectory $group
if ($LASTEXITCODE) { throw "UI detail checks failed: $OutputDirectory" }
