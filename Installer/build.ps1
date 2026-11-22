param([string]$ToolsRoot = (Join-Path $env:LOCALAPPDATA 'ScreenOffTray-Build\Tools'), [string]$RollbackSetup = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$productRoot = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PinnedZip([string]$Name, [string]$Url, [string]$Hash, [string]$Folder,
    [string]$Destination = (Join-Path $ToolsRoot $Folder)) {
    New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
    $archive = Join-Path $ToolsRoot $Name
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -UseBasicParsing -Headers @{Accept='application/octet-stream'} -Uri $Url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) { throw "Build dependency checksum differs: $Name" }
    if (!(Test-Path -LiteralPath $Destination)) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $Destination) }
    return $Destination
}

$mediaRoot = 'src/ScreenOffTray/Playback/NativeMedia/'
$mediaDll = 'vendor/ScreenOffTray.Media/screenoff_media.dll'
$mediaReceipt = Get-Content -LiteralPath (Join-Path $productRoot 'vendor/ScreenOffTray.Media/构建记录.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($item in @('Cargo.toml','Cargo.lock','src/lib.rs' | ForEach-Object { $mediaRoot + $_ }) + @($mediaDll)) {
    $pin = $mediaReceipt.Files.PSObject.Properties[$item].Value
    if ((Get-FileHash -LiteralPath (Join-Path $productRoot $item)).Hash -ne $pin) { throw "Rust source or DLL differs: $item" }
}
$mediaHash = $mediaReceipt.Files.PSObject.Properties[$mediaDll].Value
$mediaDefine = "--define=ExpectedMediaHash=$mediaHash"

$referenceRoot = Get-PinnedZip 'microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg' 'https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg' '8A7E348538E7EB91351696911689F49E3D4F63F8BAB517432BBE159B8B1104A2' 'net48-1.0.3'
$innoPackage = Get-PinnedZip 'tools.innosetup.7.1.0.nupkg' 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/7.1.0/tools.innosetup.7.1.0.nupkg' 'AAD15C662593C6F5656457A5C001F4591BABD72820CACF63F9706D4AF5D93B75' 'inno-package-7.1.0'
$iscc = Join-Path $innoPackage 'tools\ISCC.exe'
foreach ($name in @('ISCC.exe','ISCmplr.dll','ISPP.dll')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $innoPackage ('tools\' + $name))
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw "Unexpected Inno signature: $name" }
}
# Setup templates carry Inno .issig signatures rather than Authenticode.
# ISCC verifies these by default; this build never passes --no-signcheck.
$engineVersion = (& $iscc --version | Out-String).Trim()
if ($engineVersion -ne '7.1.0') { throw "Expected Inno Setup 7.1.0, got $engineVersion" }

$stageParent = Join-Path $productRoot '打包暂存'
$stage = Join-Path $stageParent ('build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$source = Join-Path $stage 'source'
$payload = Join-Path $stage 'payload'
$checks = Join-Path $stage 'checks'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $productRoot '安装包' }
foreach ($directory in @($source,$payload,$checks,$output)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
# Use the verified archive, never a previous build's extracted executable.
$unpackerRoot = Get-PinnedZip 'innounp-2.71.3-695694723.zip' 'https://raw.githubusercontent.com/jrathlev/InnoUnpacker-Windows-GUI/75ded58da3a81c029db4f73ab803b9b4154400e9/innounp-2/bin/innounp-2.zip' 'F8E45C9676BEBC7D7570108AB572673B541FCF7A7D3D24A04319AC6B5315B4E8' 'innounp-2.71.3-695694723' -Destination (Join-Path $checks 'innounp')
function Copy-SourceFile([string]$Relative) {
    $from = [IO.Path]::GetFullPath((Join-Path $productRoot $Relative))
    if (!$from.StartsWith($productRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path outside product.' }
    $to = Join-Path $source $from.Substring($productRoot.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
}

$targets = @('src\ScreenOffTray\ScreenOffTray.csproj','src\ScreenOffTray.Settings\ScreenOffTray.Settings.csproj','src\ScreenOffTray.DriverSetup\DriverSetup.csproj')
foreach ($target in $targets) {
    Copy-SourceFile $target
    $projectDirectory = Split-Path -Parent $target
    [xml]$project = Get-Content -LiteralPath (Join-Path $productRoot $target) -Raw -Encoding UTF8
    foreach ($item in @($project.SelectNodes("//*[local-name()='Compile' or local-name()='EmbeddedResource' or local-name()='Page' or local-name()='Resource' or local-name()='None']"))) {
        Copy-SourceFile (Join-Path $projectDirectory $item.Include)
    }
    foreach ($item in @($project.SelectNodes("//*[local-name()='ApplicationIcon' or local-name()='ApplicationManifest']"))) {
        Copy-SourceFile (Join-Path $projectDirectory $item.InnerText)
    }
}
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
foreach ($target in $targets) {
    & $msbuild (Join-Path $source $target) /t:Rebuild /tv:4.0 /p:Configuration=Release /p:Platform=AnyCPU /p:DebugSymbols=false /p:DebugType=none ("/p:TargetFrameworkRootPath=" + (Join-Path $referenceRoot 'build\')) /nologo /v:minimal 2>&1 | Tee-Object -FilePath (Join-Path $checks ((Split-Path -Leaf $target) + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $target" }
}
Copy-Item -LiteralPath (Join-Path $source 'src\ScreenOffTray\bin\Release\ScreenOffTray.exe') -Destination $payload
Copy-Item -LiteralPath (Join-Path $source 'src\ScreenOffTray\bin\Release\ScreenOffTray.exe.config') -Destination $payload
Copy-Item -LiteralPath (Join-Path $source 'src\ScreenOffTray.DriverSetup\bin\Release\ScreenOffTray.DriverSetup.exe') -Destination $payload
foreach($name in @('ScreenOffTray.Settings.exe','ScreenOffTray.Settings.exe.config')){Copy-Item -LiteralPath (Join-Path $source ('src\ScreenOffTray.Settings\bin\Release\'+$name)) -Destination $payload}

Copy-Item -LiteralPath (Join-Path $productRoot $mediaDll) -Destination $payload
if ((Get-FileHash -LiteralPath (Join-Path $payload 'screenoff_media.dll')).Hash -ne $mediaHash) { throw 'Staged media DLL differs' }

foreach ($subdir in @('Driver','Licenses','配置模板')) {
    New-Item -ItemType Directory -Path (Join-Path $payload $subdir) -Force | Out-Null
}
foreach ($name in @('MttVDD.inf','MttVDD.dll','mttvdd.cat')) {
    $driverPins = @{
        'MttVDD.inf'='550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
        'MttVDD.dll'='C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
        'mttvdd.cat'='08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $productRoot ('vendor\VirtualDisplayDriver\' + $name))).Hash -ne $driverPins[$name]) { throw "Pinned driver differs: $name" }
    Copy-Item -LiteralPath (Join-Path $productRoot ('vendor\VirtualDisplayDriver\' + $name)) -Destination (Join-Path $payload 'Driver')
}
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $productRoot 'vendor\Licenses') -File | Where-Object { $_.Extension -in @('.txt','.md') })) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload 'Licenses')
}
Copy-Item -LiteralPath (Join-Path $productRoot 'resources\配置模板\默认设置说明.json') -Destination (Join-Path $payload '配置模板')
Copy-Item -LiteralPath (Join-Path $productRoot 'resources\帮助.html') -Destination $payload
Copy-Item -LiteralPath (Join-Path $innoPackage 'tools\license.txt') -Destination (Join-Path $payload 'Licenses\Inno-Setup-License.txt')

$manifest = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path=$_.FullName.Substring($payload.Length+1); Length=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $output '安装内容清单.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)
& $iscc $mediaDefine ("--define=SourceRoot=" + $payload) ("--output-dir=" + $output) (Join-Path $PSScriptRoot 'ScreenOffTray.iss') 2>&1 | Tee-Object -FilePath (Join-Path $checks 'Inno-Setup.log')
if ($LASTEXITCODE -ne 0) { throw 'Inno compilation failed.' }
$setup = Join-Path $output 'ScreenOffTray-0.2.7-Setup-x64.exe'
if (!(Test-Path -LiteralPath $setup)) { throw 'Expected setup output is missing.' }
$extract = Join-Path $checks '实际安装包内容'
& (Join-Path $unpackerRoot 'innounp.exe') -x -a -y ("-d" + $extract) $setup 2>&1 | Tee-Object -FilePath (Join-Path $checks '安装包解包.log')
if ($LASTEXITCODE -ne 0) { throw 'Setup extraction failed.' }
$app = Join-Path $extract '{app}'
$actual = @(Get-ChildItem -LiteralPath $app -Recurse -File)
if ($actual.Count -ne $manifest.Count) { throw 'Unexpected number of files in the final installer.' }
foreach ($entry in $manifest) {
    $installedFile = Join-Path $app $entry.Path
    if (!(Test-Path -LiteralPath $installedFile) -or (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne $entry.SHA256) { throw "Final installer content mismatch: $($entry.Path)" }
}
function Verify-TemporaryHelper([string]$Directory, [string]$Expected) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object Name -eq 'ScreenOffTray.UpgradeCheck.exe')
    if ($files.Count -ne 1 -or (Get-FileHash -LiteralPath $files[0].FullName).Hash -ne (Get-FileHash -LiteralPath $Expected).Hash) { throw 'Temporary verifier differs.' }
}
Verify-TemporaryHelper $extract (Join-Path $payload 'ScreenOffTray.DriverSetup.exe')
if ($RollbackSetup) {
    $oldSetup = [IO.Path]::GetFullPath($RollbackSetup)
    if ((Get-FileHash -LiteralPath $oldSetup).Hash -ne '46C79B998B4A85E8E8D1A10AAEABCC5DB9C0F052DC2CA4FD5D6252E3CFEC2554') { throw 'Original 0.2.6 installer pin mismatch.' }
    $oldReference = Join-Path $checks 'rollback-reference'
    $unpacker = Join-Path $unpackerRoot 'innounp.exe'
    & $unpacker -x -a -y ("-d"+$oldReference) $oldSetup | Out-File (Join-Path $checks 'rollback-reference.log')
    if ($LASTEXITCODE) { throw 'Original rollback extraction failed.' }
    $oldApp = Join-Path $oldReference '{app}'
    $oldVerifier = Join-Path $oldApp 'ScreenOffTray.DriverSetup.exe'
    $oldManifest = @(Get-ChildItem -LiteralPath $oldApp -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($oldApp.Length+1)
        if (!$relative.StartsWith('Driver'+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
            [pscustomobject]@{Path=$relative;Length=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}
        }
    })
    & $iscc $mediaDefine ("--define=SourceRoot="+$oldApp) ("--define=VerifierFile="+$oldVerifier) '--define=ReleaseVersion=0.2.6' '--define=RollbackOnly=1' '--define=PackageSuffix=ProgramRollback-x64' ("--output-dir="+$output) (Join-Path $PSScriptRoot 'ScreenOffTray.iss') | Out-File (Join-Path $checks 'rollback-build.log')
    if ($LASTEXITCODE) { throw 'Rollback compilation failed.' }
    $rollback = Join-Path $output 'ScreenOffTray-0.2.6-ProgramRollback-x64.exe'
    $oldExtract = Join-Path $checks 'rollback-extract'
    & $unpacker -x -a -y ("-d"+$oldExtract) $rollback | Out-File (Join-Path $checks 'rollback-extract.log')
    if ($LASTEXITCODE) { throw 'Rollback extraction failed.' }
    $rollbackApp = Join-Path $oldExtract '{app}'
    if (@(Get-ChildItem -LiteralPath $rollbackApp -Recurse -File).Count -ne $oldManifest.Count) { throw 'Rollback extracted file count differs.' }
    foreach ($entry in $oldManifest) {
        if ((Get-FileHash -LiteralPath (Join-Path $rollbackApp $entry.Path)).Hash -ne $entry.SHA256) { throw 'Rollback extracted bytes differ.' }
    }
    Verify-TemporaryHelper $oldExtract $oldVerifier
    $oldManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output '回退内容清单.json') -Encoding UTF8
}
$receipt = [ordered]@{ BuiltAt=(Get-Date -Format o); Compiler=$engineVersion; Setup=$setup; SetupSHA256=(Get-FileHash -LiteralPath $setup).Hash; FileCount=$manifest.Count; ContentVerified=$true; Stage=$stage; InstalledOnHost=$false }
[IO.File]::WriteAllText((Join-Path $output '打包验证.json'), ($receipt | ConvertTo-Json -Depth 4), $utf8)
Write-Output "Installer built and extracted contents verified: $setup"
