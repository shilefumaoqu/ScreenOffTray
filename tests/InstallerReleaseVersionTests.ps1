$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$text=Get-Content -LiteralPath (Join-Path $root 'Installer/ScreenOffTray.iss') -Raw
$version=[version]([regex]::Match($text,'#define ReleaseVersion "([\d.]+)"').Groups[1].Value)
$upper=[uint32]([regex]::Match($text,'VersionLS > (\d+)').Groups[1].Value)
$build=[Math]::Max(0,$version.Build);$revision=[Math]::Max(0,$version.Revision)
$expected=($build -shl 16) -bor $revision
if($upper -ne $expected){throw 'Installer comparison threshold differs from release version'}
foreach($sample in @(@('0.1.9.13',$true),@('0.2.0.0',$true),@('0.2.0.1',$false),@('0.3.0.0',$false))){
 $v=[version]$sample[0];$ms=($v.Major -shl 16) -bor $v.Minor;$ls=($v.Build -shl 16) -bor $v.Revision
 $accepted=!($ms -gt 2 -or ($ms -eq 2 -and $ls -gt $upper))
 if($accepted -ne $sample[1]){throw ('Installer comparison mismatch '+$v)}
 'PASS numeric upgrade comparison: '+$v+' accepted='+$accepted
}
'4 static contract cases; reads actual Inno code/version; not an installation run.'
