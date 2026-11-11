param([string]$OutputDirectory = '', [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'checks/settings-open-20261108/host-regression' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary = Join-Path $OutputDirectory 'SettingsHostUiHarness.exe'
# Reuse the bounded private non-input desktop bootstrap and live DC helpers.
# No product entry, persistence, coordinator, audio COM or display backend.
$sources = @('Ui/SettingsDialog.cs','Ui/PlaybackSettingsPanel.cs','Ui/MintControls.cs','Ui/SettingsDpiContext.cs','Ui/StaticCatControl.cs','Ui/ApplicationIconCache.cs','Ui/TrayMenu.cs','HotkeyBinding.cs','AutoOffTiming.cs','Playback/PlaybackContracts.cs','tests/CSettingsUiTests.cs','tests/SettingsRefreshUiTests.cs','tests/UiDetailTests.cs','tests/SettingsHostUiTests.cs') | ForEach-Object { Join-Path $root $_ }
$resources = @('header-cat.png','header-scene.png','card-left-scene.png','card-right-scene.png','footer-flowers.png') | ForEach-Object { '/resource:' + (Join-Path $root ('Ui/Resources/theme/' + $_)) + ',ScreenOffTray.Ui.Resources.theme.' + $_ }
$log = & $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /langversion:5 /warn:4 /main:ScreenOffTray.Tests.SettingsHostUiHarness /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$binary" $resources $sources 2>&1
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'compile.txt'), (($log -join [Environment]::NewLine) + "`ncsc exit=$compileExit"))
if ($compileExit) { throw "UI host regression compile failed: $log" }
if ($CompileOnly) { return }
& $binary $OutputDirectory --group=settings-host
if ($LASTEXITCODE) { throw "UI host regression failed: $OutputDirectory" }
