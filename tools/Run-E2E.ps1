<#
.SYNOPSIS
    Run the E2E suite (tests/ProcessRecorderApp.E2E) as shards, in sequence or side by side.

.DESCRIPTION
    This script is the single source of truth for what each shard contains. CI
    (.github/workflows/build.yml) runs one matrix job per shard and must not spell the
    filters out again -- a filter written twice is two filters as soon as someone edits one.

    Shards:
      gui   Category=Gui                  (UIA-driven tests; needs an interactive desktop)
      web   the four browser/streaming classes in $WebShardClasses, minus Gui
      core  everything else, minus Gui
      all   no filter

    Class names are matched as `E2E.<Class>.` with the trailing dot on purpose: `~` is a
    substring match, so a bare `RecordingTests` would also select `ContinuousRecordingTests`.

    **A shard that selects 0 tests is a failure here.** `dotnet test --filter` exits 0 when
    it matches nothing, so an empty shard would otherwise be a silent, entirely green no-op.

    Measured, and the reason the defaults look like this:
      - Serial, the whole suite is 196 tests / about 29 minutes on the dev machine. The
        breakdown is roughly 7 minutes of fixed sleeps, 5 minutes of worker startup and
        6 minutes of GUI startup floor. The largest class is WebUiBrowserTests at 418 s.
      - Two processes side by side on 4 cores: most tests stretch by 1.1x to 1.3x. Three
        processes have not been measured.
      - The non-live generation in Mp4ProbeTests is the outlier under load (5 s -> 58 s),
        which is why it stays in `core` rather than sharing a runner with the web classes.

    Output is English on purpose -- Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI,
    so non-ASCII literals break the moment the file moves between machines.

.PARAMETER Shard
    One or more of gui, web, core, all. Default: all. Lower-cased and de-duplicated --
    ValidateSet is case-insensitive, so `core,Core` would otherwise be two runs writing the
    same log and TRX file names.
    **A list only binds when the script is called from PowerShell.** `powershell -File
    Run-E2E.ps1 -Shard core,web` hands the whole thing over as one string and fails the
    ValidateSet; use `powershell -Command "& tools\Run-E2E.ps1 -Shard core,web"` there.
    CI passes a single shard per job, so it is not affected.

.PARAMETER Parallel
    Run the requested shards at the same time instead of one after another.

.PARAMETER ExcludeFragile
    Add `Category!=Fragile` to every shard. This is what CI uses (TrayMenuTests moves a
    physical mouse cursor; its flakiness lives in the shell, not in the product).

.PARAMETER PublishDir
    The publish directory the tests drive. A relative path is resolved against the
    **repository root** (the parent of tools\), not against the current directory.
    Exported to the children as PROCESSRECORDERAPP_E2E_PUBLISH_DIR, always as an absolute
    path -- the test host's working directory is not the repository root on every machine.
    The variable is restored to its previous value (or removed) when the script returns, so
    dot-sourcing or repeated calls in one session do not leak it.

.PARAMETER NoBuild
    Skip the one-off `dotnet build` of the E2E project. Each shard always runs with
    `--no-build --no-restore`, so without this switch the build happens exactly once.

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    tools\Run-E2E.ps1 -Shard core -ExcludeFragile

.EXAMPLE
    tools\Run-E2E.ps1 -Shard gui,web,core -Parallel -ExcludeFragile
#>
[CmdletBinding()]
param(
    [ValidateSet('gui', 'web', 'core', 'all')][string[]] $Shard = @('all'),
    [switch] $Parallel,
    [switch] $ExcludeFragile,
    [string] $PublishDir = 'src/ProcessRecorderApp/bin/Release/win-x64/publish/selfcontained',
    [switch] $NoBuild,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The classes that make up the `web` shard. A static check in
# tests/ProcessRecorderApp.Tests reads this exact line, so keep the name and the shape.
$WebShardClasses = @('WebUiBrowserTests', 'DashPreviewTests', 'PreviewStreamTests', 'TranscodeTests')

# ValidateSet is case-insensitive but Select-Object -Unique is not, so lower-case first:
# `core,Core` would otherwise survive as two runs writing the same e2e-core.trx / .log.
$Shard = @($Shard | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests\ProcessRecorderApp.E2E'
$resultsDir = Join-Path $project 'TestResults'

function Get-ShardFilter {
    param([string] $Name)

    # No spaces around the operators: the filter grammar keeps whitespace inside values,
    # so `A | B` looks for a trait value that ends with a space.
    $clauses = @()
    switch ($Name) {
        'gui' { $clauses += 'Category=Gui' }
        'web' {
            $clauses += 'Category!=Gui'
            $any = @($WebShardClasses | ForEach-Object { "FullyQualifiedName~E2E.$_." })
            $clauses += '(' + ($any -join '|') + ')'
        }
        'core' {
            $clauses += 'Category!=Gui'
            $clauses += @($WebShardClasses | ForEach-Object { "FullyQualifiedName!~E2E.$_." })
        }
        'all' { }
        # A name added to the ValidateSet without a case here would fall through to an empty
        # filter -- that is `all`, i.e. the shard would silently run the whole suite.
        default { throw "unknown shard: $Name" }
    }

    if ($ExcludeFragile) { $clauses += 'Category!=Fragile' }
    return ($clauses -join '&')
}

# --- the publish the tests drive -------------------------------------------------------
if ([System.IO.Path]::IsPathRooted($PublishDir)) {
    $publishFull = $PublishDir
} else {
    $publishFull = Join-Path $repoRoot $PublishDir
}
$publishFull = [System.IO.Path]::GetFullPath($publishFull)

$exe = Join-Path $publishFull 'ProcessRecorderApp.exe'
if (-not (Test-Path $exe)) {
    throw "published exe not found: $exe -- publish first (dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-selfcontained)"
}

# The fixture reads an existing publish and never re-publishes it, so a stale exe turns
# into a green run against binaries that predate the change under test.
# Skipped on GitHub Actions: download-artifact does not preserve mtimes, so the comparison
# carries no information there.
if (-not $env:GITHUB_ACTIONS) {
    $exeStamp = (Get-Item $exe).LastWriteTimeUtc
    $newer = @(Get-ChildItem -Path (Join-Path $repoRoot 'src') -Filter '*.cs' -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $exeStamp })
    if ($newer.Count -gt 0) {
        Write-Warning "the publish is older than $($newer.Count) source file(s) (e.g. $($newer[0].FullName)); re-publish or the run is green against old binaries"
    }
}

Write-Host "publish : $publishFull"
Write-Host "shards  : $($Shard -join ', ')$(if ($Parallel) { ' (parallel)' } else { '' })"

# --- build once ------------------------------------------------------------------------
if (-not $NoBuild) {
    & dotnet build $project -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

function Start-Shard {
    param([string] $Name)

    $filter = Get-ShardFilter $Name
    $trxName = "e2e-$Name.trx"
    $log = Join-Path $resultsDir "e2e-$Name.log"
    $err = Join-Path $resultsDir "e2e-$Name.err.log"

    # **Delete the previous TRX first.** It is written only when the run ends, so a shard
    # that dies early would otherwise be scored from the last run's file.
    Get-ChildItem -Path $resultsDir -Filter $trxName -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $log, $err -Force -ErrorAction SilentlyContinue

    $arguments = @(
        'test', ('"' + $project + '"'),
        '-c', $Configuration,
        '--no-build', '--no-restore')
    if ($filter) { $arguments += @('--filter', ('"' + $filter + '"')) }
    $arguments += @(
        '--logger', ('"trx;LogFileName=' + $trxName + '"'),
        '--logger', '"console;verbosity=minimal"')

    Write-Host "start   : $Name  filter=$(if ($filter) { $filter } else { '(none)' })"
    $process = Start-Process -FilePath 'dotnet' -ArgumentList ($arguments -join ' ') `
        -WorkingDirectory $repoRoot -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $err
    # Touching Handle before the process exits is what makes ExitCode readable in 5.1.
    $null = $process.Handle

    return [pscustomobject]@{
        Shard    = $Name
        Filter   = $filter
        Process  = $process
        Log      = $log
        TrxName  = $trxName
        Started  = Get-Date
    }
}

function Get-ShardOutcome {
    param([psobject] $Run)

    $exitCode = $Run.Process.ExitCode
    $row = [pscustomobject]@{
        Shard   = $Run.Shard
        Passed  = 0
        Failed  = 0
        Skipped = 0
        Total   = 0
        Seconds = 0.0
        Exit    = $exitCode
        Note    = ''
        Ok      = $false
    }

    $trx = Get-ChildItem -Path $resultsDir -Filter $Run.TrxName -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $trx) {
        $row.Note = 'no trx (the shard did not finish)'
        return $row
    }

    [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw -Encoding UTF8
    $resultsNode = $doc.TestRun.SelectSingleNode('*[local-name()="Results"]')
    $results = @()
    if ($resultsNode) { $results = @($resultsNode.SelectNodes('*[local-name()="UnitTestResult"]')) }

    $row.Total = $results.Count
    $row.Passed = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
    $row.Skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
    $row.Failed = $row.Total - $row.Passed - $row.Skipped

    $times = $doc.TestRun.SelectSingleNode('*[local-name()="Times"]')
    if ($times) {
        $start = $times.GetAttribute('start')
        $finish = $times.GetAttribute('finish')
        if ($start -and $finish) { $row.Seconds = ([datetime]$finish - [datetime]$start).TotalSeconds }
    }

    if ($row.Total -eq 0) {
        $row.Note = 'selected 0 tests (the filter matched nothing)'
    } elseif ($row.Failed -gt 0) {
        $row.Note = 'failed tests'
    } elseif ($exitCode -ne 0) {
        $row.Note = 'non-zero exit'
    } else {
        $row.Ok = $true
    }

    return $row
}

# --- run -------------------------------------------------------------------------------
# The variable is process-wide, so a dot-sourced or repeated call would otherwise leave the
# last run's publish directory behind and point a later run at the wrong binaries.
$previousPublishDir = $env:PROCESSRECORDERAPP_E2E_PUBLISH_DIR
$env:PROCESSRECORDERAPP_E2E_PUBLISH_DIR = $publishFull
$anyShardFailed = $false
try {

$runs = @()
if ($Parallel) {
    foreach ($name in $Shard) { $runs += Start-Shard $name }
    foreach ($run in $runs) { $run.Process.WaitForExit() }
} else {
    foreach ($name in $Shard) {
        $run = Start-Shard $name
        $run.Process.WaitForExit()
        $runs += $run
    }
}

# --- report ----------------------------------------------------------------------------
$rows = @($runs | ForEach-Object { Get-ShardOutcome $_ })

Write-Host ''
Write-Host ('{0,-6} {1,7} {2,7} {3,8} {4,7} {5,9} {6,5}  {7}' -f 'shard', 'passed', 'failed', 'skipped', 'total', 'seconds', 'exit', 'note')
foreach ($row in $rows) {
    Write-Host ('{0,-6} {1,7} {2,7} {3,8} {4,7} {5,9:F0} {6,5}  {7}' -f `
        $row.Shard, $row.Passed, $row.Failed, $row.Skipped, $row.Total, $row.Seconds, $row.Exit, $row.Note)
}
Write-Host "logs    : $resultsDir\e2e-<shard>.log (stdout) / .err.log (xUnit's [FAIL] lines)"

# The children's console output went to those log files, so a red shard would otherwise
# leave nothing in this transcript to read.
foreach ($row in $rows) {
    if ($row.Ok) { continue }
    # **xUnit writes its `[FAIL]` diagnostics to stderr**, so both files have to be read;
    # the stdout file holds the per-test output. `[FAIL]` is also the only marker that does
    # not move with the console language (the `Failed` summary line is English-only).
    $log = Join-Path $resultsDir "e2e-$($row.Shard).log"
    $err = Join-Path $resultsDir "e2e-$($row.Shard).err.log"
    $present = @(@($err, $log) | Where-Object { Test-Path $_ })
    if ($present.Count -eq 0) { continue }
    $lines = @(Select-String -Path $present -Pattern '\[FAIL\]|^\s*(Failed|Error)\s' | Select-Object -First 40)
    if ($lines.Count -eq 0) { $lines = @(Get-Content -LiteralPath $present[0] -Tail 20) }
    Write-Host ''
    Write-Host "--- $($row.Shard) ---"
    foreach ($line in $lines) { Write-Host "  $line" }
}

if (@($rows | Where-Object { -not $_.Ok }).Count -gt 0) { $anyShardFailed = $true }

} finally {
    if ($null -eq $previousPublishDir) {
        Remove-Item -Path 'Env:\PROCESSRECORDERAPP_E2E_PUBLISH_DIR' -ErrorAction SilentlyContinue
    } else {
        $env:PROCESSRECORDERAPP_E2E_PUBLISH_DIR = $previousPublishDir
    }
}

if ($anyShardFailed) { exit 1 }
Write-Host ''
Write-Host 'all shards passed'
