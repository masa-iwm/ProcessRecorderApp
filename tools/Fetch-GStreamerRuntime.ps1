<#
.SYNOPSIS
    Fetch the GStreamer runtime that a "bundled" distribution ships with, and unpack it
    into src/GStreamer.GstSharpNet/runtimes/win-x64.

.DESCRIPTION
    The runtime tree is NOT in this repository -- keeping hundreds of megabytes of
    binaries out of every clone is the point. It is published as a Release asset of this
    repository and fetched from there; how the asset is produced and updated is
    docs/runtime-update.md.

    There are TWO bundled flavours, and -Flavor picks which one is unpacked:

      mingw   the official GStreamer 1.28.6 MinGW runtime (LGPL-only selection), trimmed
              to what this app can actually build: 46 files, 49.9 MB. Self-contained --
              it brings its own libgcc / libstdc++ / libwinpthread
      msvc    the official GStreamer 1.28.6 MSVC runtime, same selection, 44 files,
              24.6 MB. It has NO C/C++ runtime of its own: msvcp140.dll,
              vcruntime140.dll and vcruntime140_1.dll resolve outside the tree, so
              **this flavour needs the VC++ redistributable on the user's machine.**
              In exchange, d3d12screencapturesrc carries the WGC capture path
              (gstwinrt-1.0-0.dll, and with it the capture-api property)

    Both are the same product surface: with a fresh registry each tree registers the same
    265 elements and blacklists nothing.

    The destination is the SAME directory for both flavours, because the publish bundles
    whatever is there. Only one flavour can be staged at a time -- which is why the
    destination is emptied before unpacking (see below).

    One file in each tree is NOT an upstream binary. The d3d12 plugin
    (libgstd3d12.dll / gstd3d12.dll) is built from the 1.28.6 sources plus
    patches/gst-plugins-bad-1.28.6-d3d12-monochrome-cursor.patch, which fixes an
    out-of-bounds read that aborts the process when d3d12screencapturesrc composes a
    monochrome cursor. THIRD-PARTY-NOTICES.md carries the provenance of both.

    Neither has x264 nor libav, so a bundled build does not carry GPL plugins;
    Type=System falls through to mfh264enc. Hardware encoders (d3d11, d3d12, nvcodec,
    amfcodec, qsv, mediafoundation) are present.

    Each tree is exactly the transitive PE-import closure of the plugins this app builds
    plus the libraries it loads by name -- tools/Get-GStreamerImportClosure.ps1 computes
    it, and running that against either tree reports 0 removable files.

    OpenH264 is deliberately not bundled: its copyright licence is BSD-2, but Cisco's
    royalty-free patent arrangement assumes the user obtains Cisco's own binary, and this
    one is built from source by cerbero. See THIRD-PARTY-NOTICES.md.

    The exact file list is licenses/third-party/COMPONENTS.tsv (mingw) and
    licenses/third-party/COMPONENTS-msvc.tsv (msvc), which is also what
    .github/workflows/release.yml compares each packaged output against.

    You do NOT need this for day-to-day development. Install GStreamer (MinGW or MSVC) or
    MSYS2 (UCRT64) instead and the GstSharp.Net loader finds it -- see the resolution
    stages in src/README.md.

    The destination is emptied before unpacking. Merging into a half-populated tree is
    how a build ends up shipping a mix of two GStreamer versions -- or, now, a mix of two
    FLAVOURS, which is worse: the file names do not collide, so nothing overwrites
    anything and both cores sit in the same bin.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER Flavor
    Which bundled flavour to fetch: mingw (default) or msvc. It selects the default -Uri
    and -Sha256 below; passing either of those explicitly overrides the flavour's value.

.PARAMETER ArchivePath
    Use a local .zip instead of downloading. The archive must have win-x64 at its root.

.PARAMETER Uri
    Where to download the archive from. Defaults to the release asset of this repository
    for the chosen -Flavor.

    NOTE: the plain download works because the repository is public. Release assets of a
    private repository (e.g. a private fork) need an authenticated request and return 404
    otherwise (verified). In that case fetch the asset yourself and pass -ArchivePath:

        gh release download gstreamer-runtime-v1.28.6-r2 -p gstreamer-runtime-win-x64-v1.28.6-r2.zip
        tools/Fetch-GStreamerRuntime.ps1 -ArchivePath .\gstreamer-runtime-win-x64-v1.28.6-r2.zip

    That is exactly what .github/workflows/release.yml does, so the workflow works either way.

.PARAMETER Sha256
    Expected SHA256 of the archive. Defaults to the pinned value for the chosen -Flavor.
    Pass an empty string to skip the check (not advised).

.PARAMETER Destination
    Where to unpack. Defaults to src/GStreamer.GstSharpNet/runtimes/win-x64 next to this script.

.EXAMPLE
    pwsh tools/Fetch-GStreamerRuntime.ps1
    dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-aot

.EXAMPLE
    pwsh tools/Fetch-GStreamerRuntime.ps1 -Flavor msvc
    dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-aot
#>
[CmdletBinding()]
param(
    [ValidateSet('mingw', 'msvc')]
    [string] $Flavor = 'mingw',
    [string] $ArchivePath,
    [string] $Uri,
    [string] $Sha256,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Per-flavour release asset. Keep in sync with .github/workflows/release.yml
# (GSTREAMER_RUNTIME_TAG / GSTREAMER_RUNTIME_ASSET per flavour); the update procedure and
# the one manual gate that catches a mismatch are in docs/runtime-update.md.
$assets = @{
    mingw = @{
        Uri    = 'https://github.com/masa-iwm/ProcessRecorderApp/releases/download/gstreamer-runtime-v1.28.6-r2/gstreamer-runtime-win-x64-v1.28.6-r2.zip'
        Sha256 = '6FA7D925A1F965AE43EDE125F89F3467156D3B79BE1E22F2FBA374E8CB2CEEE5'
    }
    msvc  = @{
        Uri    = 'https://github.com/masa-iwm/ProcessRecorderApp/releases/download/gstreamer-runtime-msvc-v1.28.6/gstreamer-runtime-msvc-win-x64-v1.28.6.zip'
        Sha256 = '6A80C173D53B29DA1C2A2603889A9ADB9E92E7BA45338D329C510F89B39DB99D'
    }
}

if (-not $Uri) { $Uri = $assets[$Flavor].Uri }
# An EXPLICIT empty -Sha256 means "skip the check", so the default must not be filled in
# by testing for emptiness -- only by testing whether the caller passed the parameter.
if (-not $PSBoundParameters.ContainsKey('Sha256')) { $Sha256 = $assets[$Flavor].Sha256 }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) {
    $Destination = Join-Path $repoRoot 'src\GStreamer.GstSharpNet\runtimes\win-x64'
}
$Destination = [System.IO.Path]::GetFullPath($Destination)

# The files the build uses to decide whether the tree is really there (see
# GStreamer.GstSharpNet.csproj: GStreamerRuntimeSentinel / GStreamerRuntimeSentinelMsvc).
# Keep the two in sync. The MSVC build drops the 'lib' prefix, so the core has two
# possible names and BOTH count as "the tree is there".
$sentinels = @(
    (Join-Path $Destination 'bin\libgstreamer-1.0-0.dll'),
    (Join-Path $Destination 'bin\gstreamer-1.0-0.dll')
)

Write-Host "Flavor      : $Flavor"

$temp = $null
try {
    if ($ArchivePath) {
        $archive = [System.IO.Path]::GetFullPath($ArchivePath)
        if (-not (Test-Path $archive)) { throw "Archive not found: $archive" }
        Write-Host "Archive     : $archive (local)"
    }
    else {
        $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("pra-gst-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
        Write-Host "Downloading : $Uri"
        # Tls12 is not the default in Windows PowerShell 5.1 on older builds.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $progress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with the bar
        try { Invoke-WebRequest -Uri $Uri -OutFile $temp -UseBasicParsing }
        finally { $ProgressPreference = $progress }
        $archive = $temp
    }

    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
    Write-Host "SHA256      : $actual"
    if ($Sha256) {
        if ($actual -ne $Sha256.ToUpperInvariant()) {
            throw "SHA256 mismatch for flavor '$Flavor'. expected=$Sha256 actual=$actual"
        }
    }
    else {
        Write-Warning 'SHA256 check skipped.'
    }

    # Empty the destination first. Unpacking over an existing tree would leave files from
    # a previous version behind, and a mixed tree fails in the worst possible way: plugins
    # get blacklisted silently at runtime. Across FLAVOURS the names do not even collide,
    # so nothing would be overwritten and both trees would ship side by side.
    if (Test-Path $Destination) {
        Write-Host "Clearing    : $Destination"
        Remove-Item $Destination -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Force $Destination

    Write-Host "Unpacking   : $Destination"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        # The archive root is win-x64/, and $Destination already IS .../runtimes/win-x64,
        # so strip the first segment instead of nesting win-x64/win-x64.
        #
        # Accept '\' as a separator too. The zip format says '/', but
        # ZipFile.CreateFromDirectory on Windows PowerShell 5.1 writes '\', so the strip
        # would silently do nothing.
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName -replace '\\', '/'
            if ($name.EndsWith('/')) { continue }
            $relative = $name -replace '^win-x64/', ''
            if ($relative -eq $name) { throw "Unexpected archive layout (entry '$($entry.FullName)' is not under win-x64/)." }
            $target = Join-Path $Destination ($relative -replace '/', '\')
            $null = New-Item -ItemType Directory -Force (Split-Path -Parent $target)
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    if ($temp -and (Test-Path $temp)) { Remove-Item $temp -Force }
}

$found = @($sentinels | Where-Object { Test-Path $_ })
if ($found.Count -eq 0) {
    throw "Unpacked, but none of [$($sentinels -join ', ')] is present. The archive layout is wrong (expected win-x64/bin/... at its root)."
}
if ($found.Count -gt 1) {
    throw "Unpacked, but BOTH flavours are present in $Destination. That tree is a mix and its plugins would be blacklisted silently."
}

$files = @(Get-ChildItem $Destination -Recurse -File)
$mb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "Done. $($files.Count) files, $mb MB in $Destination"
if ($Flavor -eq 'msvc') {
    Write-Host 'NOTE: this flavour needs the VC++ redistributable (msvcp140 / vcruntime140 / vcruntime140_1) on the machine that runs it.'
}
Write-Host 'Bundled publish:'
Write-Host '  dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-aot -p:BundleGStreamerRuntime=true'
