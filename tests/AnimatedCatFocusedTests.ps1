$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'checks\c-ui-0.1.9-20261027\cat-focused'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$action=(Join-Path $root 'Ui\Resources\cat-actions.png')+',ScreenOffTray.Ui.Resources.cat-actions.png'
$variety=(Join-Path $root 'Ui\Resources\cat-variety.png')+',ScreenOffTray.Ui.Resources.cat-variety.png'
$exe=Join-Path $out 'AnimatedCatFocusedTests.exe'
& $csc /nologo /target:exe /langversion:5 /codepage:65001 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/resource:$action" "/resource:$variety" "/out:$exe" (Join-Path $root 'Ui\AnimatedCatControl.cs') (Join-Path $root 'tests\AnimatedCatFocusedTests.cs')
if($LASTEXITCODE -ne 0) { throw 'compile failed' }
& $exe
if($LASTEXITCODE -ne 0) { throw 'focused checks failed' }
