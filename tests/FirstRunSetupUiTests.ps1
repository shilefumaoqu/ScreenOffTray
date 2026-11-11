param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('FirstRunSetupUi-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary = Join-Path $OutputDirectory 'FirstRunSetupUiTests.exe'
$sources = @('Ui/FirstRunSetupDialog.cs','Ui/SettingsDpiContext.cs','Ui/MintControls.cs','Ui/TrayMenu.cs','Ui/SettingsDialog.cs','Ui/PlaybackSettingsPanel.cs','Ui/StaticCatControl.cs','Ui/ApplicationIconCache.cs','HotkeyBinding.cs','AutoOffTiming.cs','Playback/PlaybackContracts.cs','tests/FirstRunSetupUiTests.cs') | ForEach-Object { Join-Path $root $_ }
$log = & $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /langversion:5 /main:ScreenOffTray.Tests.FirstRunSetupUiTests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$binary" $sources 2>&1
$code = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'compile.txt'), (($log -join [Environment]::NewLine) + "`r`ncsc exit=$code"), [Text.Encoding]::UTF8)
if ($code -ne 0) { throw "Compile failed: $log" }
Copy-Item -LiteralPath (Join-Path $root '图标/ScreenOffTray.ico') -Destination (Join-Path $OutputDirectory 'ScreenOffTray.ico')
& $binary $OutputDirectory
$code = $LASTEXITCODE
Write-Output "Output: $OutputDirectory; exit=$code"
if ($code -ne 0) { throw "First-run UI checks failed: $OutputDirectory" }
