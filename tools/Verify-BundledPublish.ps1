<#
.SYNOPSIS
    Check a bundled publish directory against the ledger of the flavour it claims to be,
    before it is packaged into a release zip.

.DESCRIPTION
    This runs in .github/workflows/release.yml, once per bundled flavour. It exists as a
    script rather than as inline YAML because there are now TWO flavours: duplicated
    verification drifts, and the half that stops being checked is the half that ships
    unverified.

    What it checks:

      * the bundled runtime under <PublishDir>/runtimes/win-x64 matches the ledger
        EXACTLY -- missing AND extra. A count floor cannot catch "extra", and an extra
        file is precisely the case where something ships with no license text
      * every license text under licenses/third-party/ is present in the publish output
        with the same SHA256 as the one in the repository
      * license.txt and THIRD-PARTY-NOTICES.md are present

    **This is the only place that observes the license texts in the distributed output.**
    ThirdPartyLicenseTests (L1) can only see the repository.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER PublishDir
    The publish directory to check (the one that gets zipped).

.PARAMETER Ledger
    The COMPONENTS ledger for this flavour: licenses/third-party/COMPONENTS.tsv (MinGW) or
    licenses/third-party/COMPONENTS-msvc.tsv (MSVC).

.PARAMETER LicenseRoot
    Where the license texts live in the repository. Defaults to licenses/third-party next
    to this script.

.EXAMPLE
    tools\Verify-BundledPublish.ps1 `
        -PublishDir src\ProcessRecorderApp\bin\Release\win-x64\publish\bundled-msvc `
        -Ledger licenses\third-party\COMPONENTS-msvc.tsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDir,
    [Parameter(Mandatory)][string] $Ledger,
    [string] $LicenseRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $LicenseRoot) { $LicenseRoot = Join-Path $repoRoot 'licenses\third-party' }

if (-not (Test-Path $PublishDir)) { throw "publish directory not found: $PublishDir" }
if (-not (Test-Path $Ledger))     { throw "ledger not found: $Ledger" }

$PublishDir  = (Resolve-Path $PublishDir).Path.TrimEnd('\')
$LicenseRoot = (Resolve-Path $LicenseRoot).Path.TrimEnd('\')

Write-Host "publish : $PublishDir"
Write-Host "ledger  : $Ledger"

# ---- the runtime tree matches the ledger exactly ---------------------------------------
$runtimeRoot = Join-Path $PublishDir 'runtimes\win-x64'
if (-not (Test-Path $runtimeRoot)) { throw "bundled runtime missing: $runtimeRoot" }

$expected = @(Get-Content $Ledger |
    Where-Object { $_ -notmatch '^#' -and $_.Trim() -ne '' } |
    Select-Object -Skip 1 |
    ForEach-Object { ($_ -split "`t")[0] }) | Sort-Object
if ($expected.Count -eq 0) { throw "could not read a file list out of $Ledger" }

$runtimeRootResolved = (Resolve-Path $runtimeRoot).Path
$actual = @(Get-ChildItem $runtimeRoot -Recurse -File |
    ForEach-Object { $_.FullName.Substring($runtimeRootResolved.Length + 1).Replace('\', '/') }) | Sort-Object

$missing = @($expected | Where-Object { $actual -notcontains $_ })
$extra   = @($actual   | Where-Object { $expected -notcontains $_ })
if ($missing.Count -or $extra.Count) {
    foreach ($m in $missing) { Write-Host "  MISSING $m" }
    foreach ($e in $extra)   { Write-Host "  EXTRA   $e" }
    throw "bundled runtime does not match $Ledger (missing=$($missing.Count) extra=$($extra.Count))"
}
Write-Host "bundled runtime: $($actual.Count) files, matches the ledger"

# The two flavours name their core differently, so "the ledger matched" already implies the
# right one -- but say which it was, because a mixed-up pair of (fetch, ledger) arguments
# would otherwise be invisible in the log.
$core = @(Get-ChildItem (Join-Path $runtimeRoot 'bin') -Filter '*gstreamer-1.0-0.dll' -File)
if ($core.Count -ne 1) { throw "expected exactly one core library in the bundle, found $($core.Count)" }
Write-Host "flavor         : $($core[0].Name)"

# ---- the license texts came along, byte for byte ---------------------------------------
$repoLicenses = @(Get-ChildItem $LicenseRoot -Recurse -File)
$shipped = Join-Path $PublishDir 'licenses\third-party'
foreach ($f in $repoLicenses) {
    $rel = $f.FullName.Substring($LicenseRoot.Length + 1)
    $target = Join-Path $shipped $rel
    if (-not (Test-Path $target)) { throw "license text not shipped: $rel" }
    if ((Get-FileHash $target).Hash -ne (Get-FileHash $f.FullName).Hash) { throw "license text differs: $rel" }
}
Write-Host "license texts  : $($repoLicenses.Count) files"

foreach ($f in @('license.txt', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path (Join-Path $PublishDir $f))) { throw "not in the package: $f" }
}
Write-Host 'license.txt and THIRD-PARTY-NOTICES.md are in the package'
