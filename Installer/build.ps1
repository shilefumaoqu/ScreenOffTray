param([string]$ToolsRoot = (Join-Path $env:LOCALAPPDATA 'ScreenOffTray-Build\Tools'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$productRoot = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PinnedZip([string]$Name, [string]$Url, [string]$Hash, [string]$Folder) {
    New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
    $archive = Join-Path $ToolsRoot $Name
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) { throw "Build dependency checksum differs: $Name" }
    $destination = Join-Path $ToolsRoot $Folder
    if (!(Test-Path -LiteralPath $destination)) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination) }
    return $destination
}

$referenceRoot = Get-PinnedZip 'microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg' 'https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg' '8A7E348538E7EB91351696911689F49E3D4F63F8BAB517432BBE159B8B1104A2' 'net48-1.0.3'
$innoPackage = Get-PinnedZip 'tools.innosetup.7.1.0.nupkg' 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/7.1.0/tools.innosetup.7.1.0.nupkg' 'AAD15C662593C6F5656457A5C001F4591BABD72820CACF63F9706D4AF5D93B75' 'inno-package-7.1.0'
$unpackerRoot = Get-PinnedZip 'innounp-2.71.2.zip' 'https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_13/innounp-2.zip' '10FCE27B6344B7674D5625CC34AFCA1C88C12183D957C6D93E401097FA2556CD' 'innounp-2.71.2'
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
$output = Join-Path $productRoot '安装包'
foreach ($directory in @($source,$payload,$checks,$output)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
function Copy-SourceFile([string]$Relative) {
    $from = [IO.Path]::GetFullPath((Join-Path $productRoot $Relative))
    if (!$from.StartsWith($productRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path outside product.' }
    $to = Join-Path $source $from.Substring($productRoot.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
}

[xml]$project = Get-Content -LiteralPath (Join-Path $productRoot 'ScreenOffTray.csproj') -Raw
foreach ($item in @($project.SelectNodes("//*[local-name()='Compile']"))) { Copy-SourceFile $item.Include }
foreach ($item in @($project.SelectNodes("//*[local-name()='EmbeddedResource']"))) { Copy-SourceFile $item.Include }
foreach ($file in @('ScreenOffTray.csproj','App.config','app.manifest','图标\ScreenOffTray.ico')) { Copy-SourceFile $file }
# The helper project has an explicit Compile list; no test/old EXE folders enter staging.
$helperProjectPath = Join-Path $productRoot 'Installer\DriverSetup.csproj'
if (!(Test-Path -LiteralPath $helperProjectPath)) { throw 'Missing Installer\DriverSetup.csproj' }
[xml]$helperProject = Get-Content -LiteralPath $helperProjectPath -Raw
Copy-SourceFile 'Installer\DriverSetup.csproj'
foreach ($item in @($helperProject.SelectNodes("//*[local-name()='Compile']"))) {
    Copy-SourceFile ('Installer\' + $item.Include)
}
foreach ($item in @($helperProject.SelectNodes("//*[local-name()='None']") | Where-Object { $_.Include -match '\.manifest$' })) {
    Copy-SourceFile ('Installer\' + $item.Include)
}
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
foreach ($target in @('ScreenOffTray.csproj','Installer\DriverSetup.csproj')) {
    & $msbuild (Join-Path $source $target) /t:Rebuild /tv:4.0 /p:Configuration=Release /p:Platform=AnyCPU /p:DebugSymbols=false /p:DebugType=none ("/p:TargetFrameworkRootPath=" + (Join-Path $referenceRoot 'build\')) /nologo /v:minimal 2>&1 | Tee-Object -FilePath (Join-Path $checks ((Split-Path -Leaf $target) + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $target" }
}
Copy-Item -LiteralPath (Join-Path $source 'bin\Release\ScreenOffTray.exe') -Destination $payload
Copy-Item -LiteralPath (Join-Path $source 'bin\Release\ScreenOffTray.exe.config') -Destination $payload
Copy-Item -LiteralPath (Join-Path $source 'Installer\bin\Release\ScreenOffTray.DriverSetup.exe') -Destination $payload
foreach ($subdir in @('Driver','Licenses','配置模板')) {
    New-Item -ItemType Directory -Path (Join-Path $payload $subdir) -Force | Out-Null
}
foreach ($name in @('MttVDD.inf','MttVDD.dll','mttvdd.cat')) {
    $driverPins = @{
        'MttVDD.inf'='550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
        'MttVDD.dll'='C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
        'mttvdd.cat'='08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $productRoot ('Driver\' + $name))).Hash -ne $driverPins[$name]) { throw "Pinned driver differs: $name" }
    Copy-Item -LiteralPath (Join-Path $productRoot ('Driver\' + $name)) -Destination (Join-Path $payload 'Driver')
}
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $productRoot 'Licenses') -File | Where-Object { $_.Extension -in @('.txt','.md') })) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload 'Licenses')
}
Copy-Item -LiteralPath (Join-Path $productRoot '配置模板\默认设置说明.json') -Destination (Join-Path $payload '配置模板')
Copy-Item -LiteralPath (Join-Path $productRoot '帮助.html') -Destination $payload
Copy-Item -LiteralPath (Join-Path $innoPackage 'tools\license.txt') -Destination (Join-Path $payload 'Licenses\Inno-Setup-License.txt')

$manifest = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path=$_.FullName.Substring($payload.Length+1); Length=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $output '安装内容清单.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)
& $iscc ("--define=SourceRoot=" + $payload) ("--output-dir=" + $output) (Join-Path $PSScriptRoot 'ScreenOffTray.iss') 2>&1 | Tee-Object -FilePath (Join-Path $checks 'Inno-Setup.log')
if ($LASTEXITCODE -ne 0) { throw 'Inno compilation failed.' }
$setup = Join-Path $output 'ScreenOffTray-0.1.9-Setup-x64.exe'
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
$receipt = [ordered]@{ BuiltAt=(Get-Date -Format o); Compiler=$engineVersion; Setup=$setup; SetupSHA256=(Get-FileHash -LiteralPath $setup).Hash; FileCount=$manifest.Count; ContentVerified=$true; Stage=$stage; InstalledOnHost=$false }
[IO.File]::WriteAllText((Join-Path $output '打包验证.json'), ($receipt | ConvertTo-Json -Depth 4), $utf8)
Write-Output "Installer built and extracted contents verified: $setup"
