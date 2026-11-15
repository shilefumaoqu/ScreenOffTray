# Run with Windows PowerShell 5.1 as used by the root build launcher.
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $productRoot 'Installer/build.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Build script parse failed.' }
$assignment = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '[xml]$project'
}, $true)
if (!$assignment) { throw 'Actual build project loader not found.' }
# Exercise the actual loader only, without downloading tools or building/installing.
$load = [scriptblock]::Create($assignment.Extent.Text)
foreach ($target in @('src/ScreenOffTray/ScreenOffTray.csproj','src/ScreenOffTray.Settings/ScreenOffTray.Settings.csproj','src/ScreenOffTray.DriverSetup/DriverSetup.csproj')) {
    $project = $null
    . $load
    [xml]$expected = [IO.File]::ReadAllText((Join-Path $productRoot $target), [Text.Encoding]::UTF8)
    if ($project.OuterXml -cne $expected.OuterXml) { throw "Actual build loader decoded project incorrectly: $target" }
    foreach ($icon in @($project.SelectNodes("//*[local-name()='ApplicationIcon']"))) {
        $path = Join-Path (Split-Path -Parent (Join-Path $productRoot $target)) $icon.InnerText
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Decoded icon path does not exist: $target" }
    }
    Write-Output "PASS actual project loader and icon path: $target"
}
Write-Output ('PowerShell=' + $PSVersionTable.PSVersion + '; ANSI codepage=' + [Text.Encoding]::Default.CodePage)
