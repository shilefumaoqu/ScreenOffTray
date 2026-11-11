param([string]$OutputDirectory='', [switch]$Inspect)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'checks/repair-01913/dpi-prototype'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'ScreenOffTray.csproj') -Raw
$sources=@($project.SelectNodes("//*[local-name()='Compile']")|ForEach-Object{Join-Path $root $_.Include})
# This test/inspector executable and its guardian must log only beside themselves.
$bridge=Join-Path $root 'Oled/RuntimeBridge.cs'
$original=Get-Content -LiteralPath $bridge -Raw
$pattern='(?s)internal static readonly string Runtime = Path.Combine\(.*?"ScreenOffTray", "运行记录"\);'
if([regex]::Matches($original,$pattern).Count -ne 1){throw 'Runtime isolation instrumentation drift'}
$copy=[regex]::Replace($original,$pattern,'internal static readonly string Runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "证据");')
$isolated=Join-Path $OutputDirectory 'RuntimeBridge.isolated.cs'
[IO.File]::WriteAllText($isolated,$copy,[Text.UTF8Encoding]::new($true))
$sources=@($sources|Where-Object{$_ -ne $bridge})+@($isolated)+@(Get-ChildItem -LiteralPath (Join-Path $root '原型/虚拟屏缩放') -Filter '*.cs'|ForEach-Object{$_.FullName})
$refs=@($project.SelectNodes("//*[local-name()='Reference']")|ForEach-Object{'/r:'+$_.Include+'.dll'})
$exe=Join-Path $OutputDirectory 'ScreenOffTray.VirtualDpiPrototype.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /main:ScreenOffTray.VirtualDpiPrototype.PrototypeMain ('/out:'+$exe) $refs $sources *> (Join-Path $OutputDirectory 'compile.txt')
if($LASTEXITCODE){Get-Content -LiteralPath (Join-Path $OutputDirectory 'compile.txt');throw 'Prototype compile failed'}
& $exe '--self-test' | Tee-Object -FilePath (Join-Path $OutputDirectory 'tests.txt')
if($LASTEXITCODE){throw 'Prototype isolated tests failed'}
if($Inspect){& $exe '--inspect'|Tee-Object -FilePath (Join-Path $OutputDirectory 'inspect.txt');if($LASTEXITCODE){throw 'Read-only inspection failed; evidence retained'}}
'No --test-match-virtual command is run by this build/test script.'
