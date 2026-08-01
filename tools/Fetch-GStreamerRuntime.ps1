<#
.SYNOPSIS
    Fetch the GStreamer runtime that the "bundled" distribution ships with, and unpack it
    into src/GStreamer.GirCore/runtimes/win-x64.

.DESCRIPTION
    The runtime tree is NOT in this repository -- keeping hundreds of megabytes of
    binaries out of every clone is the point. It is published as a Release asset of this
    repository and fetched from there; how the asset is produced and updated is
    docs/runtime-update.md.

    What the bundled release ships is the official GStreamer 1.28.4 MinGW runtime
    (LGPL-only selection) trimmed to what this app can actually build: 45 files, 49.7 MB.
    It has no x264 and no libav, so a bundled build does not carry GPL plugins;
    Type=System falls through to mfh264enc. Hardware encoders (d3d11, d3d12, nvcodec,
    amfcodec, qsv, mediafoundation) are present.

    The tree is exactly the transitive PE-import closure of the plugins this app builds
    plus the libraries it loads by name -- tools/Get-GStreamerImportClosure.ps1 computes
    it, and running that against this tree reports 0 removable files.

    OpenH264 is deliberately not bundled: its copyright licence is BSD-2, but Cisco's
    royalty-free patent arrangement assumes the user obtains Cisco's own binary, and this
    one is built from source by cerbero. See THIRD-PARTY-NOTICES.md.

    The exact file list is licenses/third-party/COMPONENTS.tsv, which is also what
    .github/workflows/release.yml compares the packaged output against.

    You do NOT need this for day-to-day development. Install GStreamer (MinGW) or MSYS2
    (UCRT64) instead and the app finds it -- see GStreamerRuntimeLocator.

    The destination is emptied before unpacking. Merging into a half-populated tree is
    how a build ends up shipping a mix of two GStreamer versions.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER ArchivePath
    Use a local .zip instead of downloading. The archive must have win-x64 at its root.

.PARAMETER Uri
    Where to download the archive from. Defaults to the release asset of this repository.

    NOTE: the plain download works because the repository is public. Release assets of a
    private repository (e.g. a private fork) need an authenticated request and return 404
    otherwise (verified). In that case fetch the asset yourself and pass -ArchivePath:

        gh release download gstreamer-runtime-v1.28.4 -p gstreamer-runtime-win-x64-v1.28.4.zip
        tools/Fetch-GStreamerRuntime.ps1 -ArchivePath .\gstreamer-runtime-win-x64-v1.28.4.zip

    That is exactly what .github/workflows/release.yml does, so the workflow works either way.

.PARAMETER Sha256
    Expected SHA256 of the archive. Pass an empty string to skip the check (not advised).

.PARAMETER Destination
    Where to unpack. Defaults to src/GStreamer.GirCore/runtimes/win-x64 next to this script.

.EXAMPLE
    pwsh tools/Fetch-GStreamerRuntime.ps1
    dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-aot -p:BundleGStreamerRuntime=true
#>
[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Uri = 'https://github.com/masa-iwm/ProcessRecorderApp/releases/download/gstreamer-runtime-v1.28.4/gstreamer-runtime-win-x64-v1.28.4.zip',
    [string] $Sha256 = '22E099ABD5659F6A3F0B394B078D90E7B5FD0B52ABF418A9ACD085607F1A4AC7',
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) {
    $Destination = Join-Path $repoRoot 'src\GStreamer.GirCore\runtimes\win-x64'
}
$Destination = [System.IO.Path]::GetFullPath($Destination)

# The file the build uses to decide whether the tree is really there (see
# GStreamer.GirCore.csproj: GStreamerRuntimeSentinel). Keep the two in sync.
$sentinel = Join-Path $Destination 'bin\libgstreamer-1.0-0.dll'

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
            throw "SHA256 mismatch. expected=$Sha256 actual=$actual"
        }
    }
    else {
        Write-Warning 'SHA256 check skipped.'
    }

    # Empty the destination first. Unpacking over an existing tree would leave files from
    # a previous version behind, and a mixed tree fails in the worst possible way: plugins
    # get blacklisted silently at runtime.
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

if (-not (Test-Path $sentinel)) {
    throw "Unpacked, but $sentinel is missing. The archive layout is wrong (expected win-x64/bin/... at its root)."
}

$files = @(Get-ChildItem $Destination -Recurse -File)
$mb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "Done. $($files.Count) files, $mb MB in $Destination"
Write-Host 'Bundled publish:'
Write-Host '  dotnet publish src/ProcessRecorderApp/ProcessRecorderApp.csproj -p:PublishProfile=win-x64-aot -p:BundleGStreamerRuntime=true'
