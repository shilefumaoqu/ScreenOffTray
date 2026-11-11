param([string]$OutputDirectory = '',[switch]$Baseline,[switch]$ComboDiagnostic,[switch]$ComboConfirm)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'checks/finish-20261107/settings-refresh-final' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary = Join-Path $OutputDirectory 'SettingsRefreshUiHarness.exe'
$sources = @('Ui/SettingsDialog.cs','Ui/PlaybackSettingsPanel.cs','Ui/MintControls.cs','Ui/SettingsDpiContext.cs','Ui/StaticCatControl.cs','Ui/ApplicationIconCache.cs','Ui/TrayMenu.cs','HotkeyBinding.cs','AutoOffTiming.cs','Playback/PlaybackContracts.cs','tests/CSettingsUiTests.cs','tests/SettingsRefreshUiTests.cs','tests/UiDetailTests.cs') | ForEach-Object { Join-Path $root $_ }
if ($ComboDiagnostic) {
    # Instrument only the compiled diagnostic copy, never the product source.
    $mintPath = Join-Path $root 'Ui/MintControls.cs'
    $tracePath = (Join-Path $OutputDirectory 'combo-create-trace.txt').Replace('"','""')
    $traceCode = 'System.IO.File.AppendAllText(@"' + $tracePath + '", "CREATE parent="+(Parent==null ? "null" : Parent.Name)+" form="+(FindForm()==null ? "null" : "attached")+" parentHandle="+(Parent!=null && Parent.IsHandleCreated)+" threadContext="+SettingsDpiContext.CaptureCurrent()+Environment.NewLine+Environment.StackTrace+Environment.NewLine);'
    $mintText = [IO.File]::ReadAllText($mintPath).Replace("`r`n","`n")
    $mintText = $mintText.Replace('protected override void CreateHandle()' + "`n        {", 'protected override void CreateHandle()' + "`n        {`n            " + $traceCode)
    if (!$mintText.Contains($traceCode)) { throw 'Combo CreateHandle diagnostic instrumentation did not match.' }
    $instrumented = Join-Path $OutputDirectory 'MintControls-diagnostic.cs'
    [IO.File]::WriteAllText($instrumented,$mintText)
    $sources = @($sources | ForEach-Object { if ($_ -eq $mintPath) { $instrumented } else { $_ } })
}
$resources = @('header-cat.png','header-scene.png','card-left-scene.png','card-right-scene.png','footer-flowers.png') | ForEach-Object { '/resource:' + (Join-Path $root ('Ui/Resources/theme/' + $_)) + ',ScreenOffTray.Ui.Resources.theme.' + $_ }
$log = & $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /langversion:5 /warn:4 /main:ScreenOffTray.Tests.CSettingsUiTests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$binary" $resources $sources 2>&1
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'compile.txt'), ($log -join [Environment]::NewLine))
if ($LASTEXITCODE) { throw "UI-only compile failed: $log" }
$group = if ($ComboDiagnostic) { '--group=refresh-combo-diagnostic' } elseif ($ComboConfirm) { '--group=refresh-combo-confirm' } elseif ($Baseline) { '--group=refresh-baseline' } else { '--group=refresh-final' }
& $binary $OutputDirectory $group
if ($LASTEXITCODE) { throw "UI regression failed: $OutputDirectory" }
