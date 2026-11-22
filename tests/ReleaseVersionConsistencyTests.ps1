param([switch]$Candidate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Text([string]$relative) {
    return Get-Content -LiteralPath (Join-Path $root $relative) -Raw -Encoding UTF8
}
function Expect([string]$text, [string]$pattern, [string]$name) {
    if ($text -notmatch $pattern) { throw "Release version mismatch: $name" }
}

$assembly = Read-Text 'app_main/Shared/AssemblyInfo.cs'
$version = [regex]::Match($assembly, 'AssemblyFileVersion\("(\d+\.\d+\.\d+)\.0"\)').Groups[1].Value
if (!$version) { throw 'AssemblyFileVersion was not found.' }
$fullVersion = $version + '.0'
$installer = Read-Text 'Installer/ScreenOffTray.iss'
$installerVersion = [regex]::Match($installer, '#define ReleaseVersion "(\d+\.\d+\.\d+)"').Groups[1].Value
if ($installerVersion -ne $version) { throw 'Installer and assembly release versions differ.' }
$parts = [version]$version
$revision = [Math]::Max(0, $parts.Revision)
$expectedLow = ($parts.Build -shl 16) -bor $revision
$threshold = [uint32]([regex]::Match($installer, 'VersionLS > (\d+)').Groups[1].Value)
if ($threshold -ne $expectedLow) { throw 'Installer upgrade threshold differs from release version.' }

Expect (Read-Text 'app_main/ScreenOffTray.Settings/app.manifest') ('<assemblyIdentity version="'+[regex]::Escape($fullVersion)+'" name="ScreenOffTray.Settings"') 'settings manifest'
Expect (Read-Text 'app_main/ScreenOffTray.Settings/SettingsWindow.xaml') ([regex]::Escape($version)+'.*Windows x64') 'settings UI'
Expect (Read-Text 'app_main/ScreenOffTray.Settings/SettingsWindow.cs') ('Version '+[regex]::Escape($version)+'.*Windows x64') 'settings English UI'
Expect (Read-Text 'Installer/build.ps1') ('ScreenOffTray-'+[regex]::Escape($version)+'-Setup-x64\.exe') 'build output'
$helpFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'resources') -Filter '*.html' -File)
if ($helpFiles.Count -ne 1) { throw 'Expected one public help file.' }
$helpPath = $helpFiles[0].FullName
$help = Get-Content -LiteralPath $helpPath -Raw -Encoding UTF8
Expect $help ('ScreenOffTray '+[regex]::Escape($version)+'</h1>') 'Chinese help title'
Expect $help ('Version '+[regex]::Escape($version)+' targets Windows') 'English help title'
if (!$Candidate) { foreach ($relative in @('README.md','README_EN.md')) {
    $readme = Read-Text $relative
    Expect $readme ('releases/tag/v'+[regex]::Escape($version)) ($relative + ' release link')
    Expect $readme ('ScreenOffTray-'+[regex]::Escape($version)+'-Setup-x64\.exe') ($relative + ' installer name')
}
}
Write-Output ('PASS release '+$version+' is consistent across assembly, manifest, settings, help, installer, builder (README links checked for published releases)')
