$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $root ('checks/build-dependency-tests/' + [Guid]::NewGuid().ToString('N'))
$ToolsRoot = Join-Path $testRoot 'tools'
$fixture = Join-Path $testRoot 'fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'marker.txt'), 'fixture')
$fixtureZip = Join-Path $testRoot 'fixture.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($fixture, $fixtureZip)
$hash = (Get-FileHash -LiteralPath $fixtureZip -Algorithm SHA256).Hash
$script:downloads = 0
# Local fixture only: never contact a server or execute downloaded code.
function Invoke-WebRequest {
    param([switch]$UseBasicParsing, $Headers, $Uri, $OutFile)
    if ($Headers.Accept -ne 'application/octet-stream') { throw 'Missing binary Accept header.' }
    $script:downloads++
    Copy-Item -LiteralPath $fixtureZip -Destination $OutFile
}
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'Installer/build.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Build script parse failed.' }
$helper = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-PinnedZip'}, $true)
if (!$helper) { throw 'Actual dependency loader not found.' }
. ([scriptblock]::Create($helper.Extent.Text))
$destination = Get-PinnedZip 'tool.zip' 'https://example.invalid/tool.zip' $hash 'tool'
if ($script:downloads -ne 1 -or [IO.File]::ReadAllText((Join-Path $destination 'marker.txt')) -ne 'fixture') { throw 'Cold download/extraction failed.' }
$cached = Get-PinnedZip 'tool.zip' 'https://example.invalid/tool.zip' $hash 'tool'
if ($cached -ne $destination -or $script:downloads -ne 1) { throw 'Valid cache was not reused.' }
[IO.File]::WriteAllText((Join-Path $ToolsRoot 'tool.zip'), 'corrupted')
try {
    Get-PinnedZip 'tool.zip' 'https://example.invalid/tool.zip' $hash 'must-not-extract' | Out-Null
    throw 'Hash mismatch was accepted.'
} catch {
    if ($_.Exception.Message -notlike 'Build dependency checksum differs:*') { throw }
}
if (Test-Path -LiteralPath (Join-Path $ToolsRoot 'must-not-extract')) { throw 'Unverified archive was extracted.' }
Write-Output 'PASS actual loader: cold download, binary header, valid cache reuse, hash mismatch rejected before extraction.'
