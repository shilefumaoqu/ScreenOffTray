param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'checks/static-cat-20261108/control' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$binary = Join-Path $OutputDirectory 'StaticCatTests.exe'
$resource = '/resource:' + (Join-Path $root 'Ui/Resources/theme/header-cat.png') + ',ScreenOffTray.Ui.Resources.theme.header-cat.png'
& $compiler /nologo /target:exe /optimize+ /langversion:5 /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $resource "/out:$binary" (Join-Path $root 'Ui/StaticCatControl.cs') (Join-Path $root 'tests/StaticCatTests.cs')
if ($LASTEXITCODE) { throw 'Static cat compilation failed.' }
& $binary $OutputDirectory | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if ($LASTEXITCODE) { throw 'Static cat checks failed.' }
