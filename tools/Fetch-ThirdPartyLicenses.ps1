<#
.SYNOPSIS
    Download the upstream license texts for the bundled GStreamer runtime into
    licenses/third-party/, verifying each file against a pinned SHA256.

.DESCRIPTION
    The bundled distribution ships third-party binaries, so it has to ship their license
    texts. The official GStreamer MinGW installer does NOT contain any: all 712 files it
    installs were scanned and not one is a COPYING/LICENSE/NOTICE. So the texts have to
    come from upstream, and they are checked in under licenses/third-party/ rather than
    baked into the runtime archive -- that keeps the archive an unmodified copy of the
    upstream binaries and lets the tests guard the texts.

    This script only refreshes them. Day-to-day you never need to run it; the files are
    in the repository. Run it when a bundled component is upgraded, then update
    licenses/third-party/SOURCES.tsv and THIRD-PARTY-NOTICES.md together.

    Every entry, its version and its URL live in licenses/third-party/SOURCES.tsv, which
    ThirdPartyLicenseTests reads as well -- the pinned hashes exist in exactly one place.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER Verify
    Do not download anything. Check the files already on disk against SOURCES.tsv and
    report. This is what you want when you just need to know whether the tree is intact.

.PARAMETER SourcesFile
    Defaults to licenses/third-party/SOURCES.tsv next to this script's repository root.

.EXAMPLE
    pwsh tools/Fetch-ThirdPartyLicenses.ps1 -Verify

.EXAMPLE
    pwsh tools/Fetch-ThirdPartyLicenses.ps1
#>
[CmdletBinding()]
param(
    [switch] $Verify,
    [string] $SourcesFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourcesFile) {
    $SourcesFile = Join-Path $repoRoot 'licenses\third-party\SOURCES.tsv'
}
$SourcesFile = [System.IO.Path]::GetFullPath($SourcesFile)
if (-not (Test-Path $SourcesFile)) { throw "Not found: $SourcesFile" }
$destRoot = Split-Path -Parent $SourcesFile

$tab = [string][char]9

# Read SOURCES.tsv. '#' starts a comment; the first non-comment line is the header.
$rows = @()
$header = $null
foreach ($line in (Get-Content -LiteralPath $SourcesFile)) {
    if ($line.StartsWith('#') -or $line.Trim() -eq '') { continue }
    $fields = $line.Split($tab)
    if (-not $header) { $header = $fields; continue }
    $row = @{}
    for ($i = 0; $i -lt $header.Length; $i++) {
        $row[$header[$i]] = if ($i -lt $fields.Length) { $fields[$i] } else { '' }
    }
    $rows += ,$row
}
if ($rows.Count -eq 0) { throw "No entries in $SourcesFile" }
Write-Host "Sources     : $SourcesFile ($($rows.Count) files)"
Write-Host "Destination : $destRoot"
Write-Host ''

# TLS 1.2 is not the default in Windows PowerShell 5.1 on older builds.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$failures = @()

foreach ($row in $rows) {
    $target = Join-Path (Join-Path $destRoot $row.Project) $row.File
    $label = "$($row.Project)/$($row.File)"

    if ($Verify) {
        if (-not (Test-Path -LiteralPath $target)) {
            $failures += "$label : missing"
            Write-Host ("  MISSING  {0}" -f $label)
            continue
        }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($actual -ne $row.Sha256.ToUpperInvariant()) {
            $failures += "$label : sha256 expected=$($row.Sha256) actual=$actual"
            Write-Host ("  CHANGED  {0}" -f $label)
        }
        else {
            Write-Host ("  ok       {0}  ({1})" -f $label, $row.Version)
        }
        continue
    }

    $null = New-Item -ItemType Directory -Force (Split-Path -Parent $target)
    $temp = $null
    try {
        if ($row.ArchiveMember) {
            # The file only exists inside a source archive. Verify the archive against the
            # checksum the build pins for it before trusting anything that comes out.
            $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('pra-lic-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
            $null = New-Item -ItemType Directory -Force $temp
            $archive = Join-Path $temp (Split-Path -Leaf ([uri]$row.Uri).AbsolutePath)
            Write-Host ("  fetching {0}  <- {1}" -f $label, $row.Uri)
            $progress = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with the bar
            try { Invoke-WebRequest -Uri $row.Uri -OutFile $archive -UseBasicParsing }
            finally { $ProgressPreference = $progress }

            $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
            if ($archiveHash -ne $row.ArchiveSha256.ToUpperInvariant()) {
                throw "archive sha256 mismatch for $label. expected=$($row.ArchiveSha256) actual=$archiveHash"
            }

            # bsdtar ships with Windows 10+ and reads gz/bz2/xz.
            & tar.exe -xf $archive -C $temp $row.ArchiveMember
            if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $($row.ArchiveMember) from $archive" }
            $extracted = Join-Path $temp ($row.ArchiveMember -replace '/', '\')
            if (-not (Test-Path -LiteralPath $extracted)) { throw "not in archive: $($row.ArchiveMember)" }
            Copy-Item -LiteralPath $extracted -Destination $target -Force
        }
        else {
            Write-Host ("  fetching {0}  <- {1}" -f $label, $row.Uri)
            $progress = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'
            try { Invoke-WebRequest -Uri $row.Uri -OutFile $target -UseBasicParsing }
            finally { $ProgressPreference = $progress }
        }
    }
    finally {
        if ($temp -and (Test-Path -LiteralPath $temp)) { Remove-Item -LiteralPath $temp -Recurse -Force }
    }

    # Several of these hosts answer a blocked request with 200 and an HTML challenge page,
    # so the status code proves nothing. The hash is the only check that does.
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($actual -ne $row.Sha256.ToUpperInvariant()) {
        $failures += "$label : sha256 expected=$($row.Sha256) actual=$actual"
        Write-Host ("  CHANGED  {0}" -f $label)
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "  $f" }
    throw "$($failures.Count) of $($rows.Count) license files did not match SOURCES.tsv."
}
Write-Host "All $($rows.Count) license files match SOURCES.tsv."
