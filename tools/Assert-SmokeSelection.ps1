<#
.SYNOPSIS
    Fail unless the release smoke run actually selected the tests it is supposed to.

.DESCRIPTION
    **`--filter` succeeds when it selects nothing.** The smoke step in
    .github/workflows/release.yml is the only test that runs against a bundled publish
    (docs/coverage-gaps.md), so a class rename that makes the filter match nothing would
    ship a green, entirely unverified zip.

    This checks the TRX for the classes that filter is supposed to select -- not a count
    floor, which cannot catch "one of the three stopped running". It runs once per bundled
    flavour, which is why it is a script: the same check written twice in YAML is the same
    check until someone edits one of them.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER TrxName
    File name of the TRX the smoke step wrote (--logger "trx;LogFileName=...").

.PARAMETER ResultsDir
    Where to look for it. Defaults to tests/ProcessRecorderApp.E2E/TestResults.

.PARAMETER ExpectedClasses
    The classes the filter is supposed to select. The default is the set that
    `~SmokeTests|~RuntimeResolutionTests` matches (`~SmokeTests` also matches
    `GuiSmokeTests`). Change the filter, change this.

.EXAMPLE
    tools\Assert-SmokeSelection.ps1 -TrxName release-smoke-msvc.trx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TrxName,
    [string] $ResultsDir,
    [string[]] $ExpectedClasses = @('SmokeTests', 'GuiSmokeTests', 'RuntimeResolutionTests')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ResultsDir) { $ResultsDir = Join-Path $repoRoot 'tests\ProcessRecorderApp.E2E\TestResults' }

if (-not (Test-Path $ResultsDir)) { throw "test results not found: $ResultsDir" }
$trx = Get-ChildItem -Path $ResultsDir -Filter $TrxName -Recurse | Select-Object -First 1
if (-not $trx) { throw "$TrxName not found; the smoke test did not run" }

[xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw -Encoding UTF8
$names = @($doc.TestRun.Results.UnitTestResult | ForEach-Object { $_.testName })
if ($names.Count -eq 0) { throw "the smoke test selected 0 tests (the filter matched nothing): $TrxName" }

# testName is the fully qualified name (with parentheses when the test is a theory case).
$classes = @($names |
    ForEach-Object { ($_ -split '\(')[0] } |
    ForEach-Object { $parts = $_ -split '\.'; if ($parts.Count -ge 2) { $parts[$parts.Count - 2] } }) |
    Sort-Object -Unique
Write-Host "$TrxName : ran $($names.Count) tests in [$($classes -join ', ')]"

$missing = @($ExpectedClasses | Where-Object { $classes -notcontains $_ })
if ($missing.Count) {
    throw "the smoke filter selected nothing from: $($missing -join ', ') (renamed or moved?)"
}
