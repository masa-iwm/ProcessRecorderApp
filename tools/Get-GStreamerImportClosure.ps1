<#
.SYNOPSIS
    Work out which files of a GStreamer runtime tree the bundled distribution has to keep,
    by following PE imports from the seeds this app actually loads.

.DESCRIPTION
    This is how licenses/third-party/COMPONENTS.tsv was produced. Run it again when the
    GStreamer version changes, when a plugin is added to the set the product can build, or
    to check that the shipped tree is still exactly the closure and nothing else.

    The seeds are:

      * every plugin under lib/gstreamer-1.0 (or -SeedPlugins when the tree still holds
        plugins the product never builds -- the untrimmed installer output does)
      * the libraries named from managed code: the module set the GstSharp.Net binding
        registers (libgstreamer-1.0-0.dll, libgstbase-1.0-0.dll, libgstapp-1.0-0.dll,
        libgobject-2.0-0.dll, libgmodule-2.0-0.dll, libglib-2.0-0.dll). The app's own
        code names no GStreamer library any more: the binding is the only one that does.
        Those are MinGW names; on an MSVC tree the same files have no 'lib' prefix and
        are found under the stripped name
      * bin/gst-inspect-1.0.exe, which tools/Verify-GpuEncoders.ps1 runs out of the
        bundled tree during the real-machine check
      * bin/gst-launch-1.0.exe, shipped so that a pipeline can be reproduced stand-alone
        on a user's machine (field diagnostics; see docs/environment-facts.md for the
        kind of repro that needs it)

    Imports that are not files of the tree are reported separately instead of being
    ignored. That matters: a name this script failed to parse would otherwise disappear
    from the closure and become a file someone deletes.

    WHAT THIS CANNOT SEE: a library opened at run time by name (g_module_open /
    LoadLibrary) is not a PE import, so it does not appear here. Before deleting anything,
    also search the surviving binaries for the names being deleted, compare the element
    inventory of both trees with a FRESH registry (a stale one lists elements whose plugin
    no longer loads), and confirm the blacklist is empty -- a plugin that cannot resolve a
    dependency is blacklisted silently. See THIRD-PARTY-NOTICES.md, "6.".

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER RuntimeRoot
    The tree to analyse: the directory that has bin\ and lib\gstreamer-1.0\ under it.

.PARAMETER Objdump
    objdump.exe from binutils. MSYS2 has it (pacman -S binutils); the GStreamer MinGW
    installer does not ship one.

    Ignored when -Dumpbin is given.

.PARAMETER Dumpbin
    dumpbin.exe from Visual Studio, as an alternative to objdump. Any machine that can
    link a Native AOT publish already has one; a machine with the MSVC GStreamer and no
    MSYS2 has no objdump at all. Both readers are given the same treatment: the import
    list of a file that cannot be read is an ERROR, never an empty list.

.PARAMETER SeedPlugins
    File names of the plugins to seed with. Defaults to every plugin in the tree.

.PARAMETER OutDir
    Write closure.txt / removable.txt / external.txt / edges.csv here.

.EXAMPLE
    tools\Get-GStreamerImportClosure.ps1 -RuntimeRoot src\GStreamer.GstSharpNet\runtimes\win-x64

.EXAMPLE
    An untrimmed MSVC installation, on a machine that has Visual Studio but no MSYS2:

    tools\Get-GStreamerImportClosure.ps1 ``
        -RuntimeRoot $env:LOCALAPPDATA\Programs\gstreamer\1.0\msvc_x86_64 ``
        -Dumpbin 'C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\dumpbin.exe' ``
        -SeedPlugins gstd3d12.dll,gstd3d11.dll,... ``
        -OutDir out

.OUTPUTS
    A summary on stdout. Exit code is 0 even when files are removable -- this reports, it
    does not delete.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RuntimeRoot,
    [string] $Objdump = 'objdump.exe',
    [string] $Dumpbin,
    [string[]] $SeedPlugins,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RuntimeRoot = (Resolve-Path $RuntimeRoot).Path.TrimEnd('\')

# Pick the import reader. -Dumpbin wins when given; otherwise objdump, which is the
# historical default. A machine with the MSVC GStreamer and no MSYS2 has no objdump,
# and every machine that can link a Native AOT publish has dumpbin.
if ($Dumpbin) {
    $readerCommand = Get-Command $Dumpbin -ErrorAction SilentlyContinue
    if (-not $readerCommand) { throw "dumpbin not found: '$Dumpbin'." }
    $ReaderMode = 'dumpbin'
}
else {
    $readerCommand = Get-Command $Objdump -ErrorAction SilentlyContinue
    if (-not $readerCommand) {
        throw "objdump not found: '$Objdump'. Install MSYS2 binutils and pass -Objdump <path to objdump.exe>, or pass -Dumpbin <path to dumpbin.exe> from Visual Studio."
    }
    $ReaderMode = 'objdump'
}
$ReaderExe = $readerCommand.Source

# Run the reader through System.Diagnostics.Process, NOT the PowerShell pipeline with a
# stderr redirect. In Windows PowerShell 5.1, redirecting a native command's stderr
# (2>$null included) wraps every line in an ErrorRecord, and with
# $ErrorActionPreference='Stop' the first warning it prints would terminate the whole
# walk (.claude/rules/powershell.md bans 2>&1 for the same mechanism).
# Also fail loudly when the reader cannot parse a file: a silently-empty import list makes
# the closure smaller, and the closure is what decides which files may be DELETED from
# the bundled runtime -- the exact failure mode this script's own docs call the top risk.
# An EMPTY import list is an error for the same reason: every PE in these trees imports at
# least KERNEL32, so "no imports" means the output was not understood (a localized dumpbin
# header, a format change) rather than a leaf binary.
function Get-ImportedDllNames {
    param([string] $Path)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $ReaderExe
    if ($ReaderMode -eq 'dumpbin') { $psi.Arguments = '/nologo /dependents "' + $Path + '"' }
    else                           { $psi.Arguments = '-p "' + $Path + '"' }
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    # Async reads before waiting, so neither pipe can fill up and deadlock the process.
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $out = $outTask.Result
    $errText = $errTask.Result
    if ($p.ExitCode -ne 0) {
        throw "$ReaderMode failed for '$Path' (exit $($p.ExitCode)): $($errText.Trim())"
    }

    $names = New-Object System.Collections.ArrayList
    if ($ReaderMode -eq 'dumpbin') {
        # dumpbin prints one block per import table:
        #   "  Image has the following dependencies:"   / "... delay load dependencies:"
        #   ""
        #   "    NAME.dll"
        #   ""
        # objdump's "DLL Name:" covers the delay import table too, so both blocks are read
        # here -- otherwise the two readers would not produce the same closure.
        $inBlock = $false
        foreach ($line in ($out -split "`r?`n")) {
            if ($line -match 'Image has the following .*dependencies:') { $inBlock = $true; continue }
            if (-not $inBlock) { continue }
            if ($line -match '^\s*$') { continue }
            if ($line -match '^\s+(\S+\.(?:dll|exe))\s*$') { $null = $names.Add($Matches[1]) }
            else { $inBlock = $false }   # "Summary", "Header values", ...
        }
    }
    else {
        foreach ($line in ($out -split "`r?`n")) {
            if ($line -match '^\s*DLL Name:\s*(\S+)\s*$') { $null = $names.Add($Matches[1]) }
        }
    }
    if ($names.Count -eq 0) {
        throw "$ReaderMode reported no imports for '$Path'. Every PE in a GStreamer tree imports at least KERNEL32, so its output was not understood -- this is not an empty import list."
    }
    return @($names)
}

function Get-RelativePath {
    param([string] $FullName)
    ($FullName.Substring($RuntimeRoot.Length + 1)) -replace '\\', '/'
}

# ---- index the tree: file name (lower case) -> relative path(s) ----------------------
$all = @(Get-ChildItem -Path $RuntimeRoot -Recurse -File)
$byName = @{}
foreach ($f in $all) {
    $key = $f.Name.ToLowerInvariant()
    if (-not $byName.ContainsKey($key)) { $byName[$key] = New-Object System.Collections.ArrayList }
    $null = $byName[$key].Add((Get-RelativePath $f.FullName))
}

# ---- seeds ---------------------------------------------------------------------------
$pluginFiles = @($all | Where-Object { $_.FullName -like "$RuntimeRoot\lib\gstreamer-1.0\*" })
$seeds = New-Object System.Collections.ArrayList
foreach ($p in $pluginFiles) {
    if ($SeedPlugins -and ($SeedPlugins -notcontains $p.Name)) { continue }
    $null = $seeds.Add((Get-RelativePath $p.FullName))
}
if ($SeedPlugins) {
    foreach ($want in $SeedPlugins) {
        if ($pluginFiles.Name -notcontains $want) { Write-Warning "seed plugin not in tree: $want" }
    }
}

# Named from managed code -- the modules the GstSharp.Net binding loads by name
# (source of truth: NativeNames.cs in the GstSharp.Net repo, MinGW column,
# restricted to the assemblies this app references: GLib/GObject/GModule/Gst/
# GstBase/GstApp) -- plus the two tools shipped on purpose:
# gst-inspect-1.0.exe for Verify-GpuEncoders.ps1 and gst-launch-1.0.exe for
# stand-alone pipeline repros on a user's machine. RuntimeClosureSeedSyncTests (L1)
# pins this list.
$namedSeeds = @(
    'bin/libgstreamer-1.0-0.dll',
    'bin/libgstbase-1.0-0.dll',
    'bin/libgstapp-1.0-0.dll',
    'bin/libgobject-2.0-0.dll',
    'bin/libgmodule-2.0-0.dll',
    'bin/libglib-2.0-0.dll',
    'bin/gst-inspect-1.0.exe',
    'bin/gst-launch-1.0.exe'
)
#
# The list above is MinGW naming. The MSVC build of GStreamer ships the very same
# libraries WITHOUT the 'lib' prefix (gstreamer-1.0-0.dll, glib-2.0-0.dll, ...), so each
# seed is looked up under both names and the one that exists is used. Deriving the MSVC
# name here keeps ONE literal list in this file -- the list the L1 test parses.
#
# On the 1.28.6 trees this list changes nothing about the RESULT: every one of the six is
# also reached by a PE import from some plugin (measured, both flavours), so a walk seeded
# with the plugins alone produces the identical 44/46 files. The list is here for the case
# it does not -- the binding loads these BY NAME, which no import table records -- and
# that guarantee only holds if a lookup that fails is loud. Hence the throw: without the
# MSVC derivation an MSVC tree would emit six "seed not in tree" warnings on every run,
# and warnings that are always there are warnings nobody reads.
foreach ($s in $namedSeeds) {
    $candidates = @($s)
    $leaf = $s.Substring($s.LastIndexOf('/') + 1)
    if ($leaf.StartsWith('lib')) { $candidates += ($s.Substring(0, $s.Length - $leaf.Length) + $leaf.Substring(3)) }

    $found = $null
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $RuntimeRoot ($c -replace '/', '\'))) { $found = $c; break }
    }
    if (-not $found) { throw "named seed not in tree under either naming: $($candidates -join ' / ')" }
    $null = $seeds.Add($found)
}
if ($seeds.Count -eq 0) { throw "No seeds. Is '$RuntimeRoot' really a GStreamer runtime tree?" }

# ---- breadth-first walk over the import tables ---------------------------------------
$closure = @{}
$external = @{}
$edges = New-Object System.Collections.ArrayList
$queue = New-Object System.Collections.Queue
foreach ($s in $seeds) { $queue.Enqueue($s) }

while ($queue.Count -gt 0) {
    $rel = $queue.Dequeue()
    if ($closure.ContainsKey($rel)) { continue }
    $closure[$rel] = $true

    $full = Join-Path $RuntimeRoot ($rel -replace '/', '\')
    $imports = @(Get-ImportedDllNames -Path $full)

    foreach ($imp in $imports) {
        $key = $imp.ToLowerInvariant()
        if ($byName.ContainsKey($key)) {
            foreach ($target in $byName[$key]) {
                $null = $edges.Add([pscustomobject]@{ From = $rel; To = $target })
                if (-not $closure.ContainsKey($target)) { $queue.Enqueue($target) }
            }
        }
        else {
            if (-not $external.ContainsKey($key)) { $external[$key] = 0 }
            $external[$key]++
        }
    }
}

# ---- report --------------------------------------------------------------------------
function Get-TotalBytes {
    param([string[]] $Relative)
    if (-not $Relative -or $Relative.Count -eq 0) { return 0 }
    ($Relative | ForEach-Object { (Get-Item (Join-Path $RuntimeRoot ($_ -replace '/', '\'))).Length } |
        Measure-Object -Sum).Sum
}

$kept = @($closure.Keys | Sort-Object)
$removable = @(@($all | ForEach-Object { Get-RelativePath $_.FullName }) |
    Where-Object { -not $closure.ContainsKey($_) } | Sort-Object)

Write-Host "tree      : $RuntimeRoot"
Write-Host ("files     : {0}" -f $all.Count)
Write-Host ("seeds     : {0}" -f $seeds.Count)
Write-Host ("closure   : {0} files, {1:N0} bytes" -f $kept.Count, (Get-TotalBytes $kept))
Write-Host ("removable : {0} files, {1:N0} bytes" -f $removable.Count, (Get-TotalBytes $removable))
Write-Host ''
Write-Host 'imports resolved outside the tree (every one of these must be a Windows system DLL):'
foreach ($name in ($external.Keys | Sort-Object)) { Write-Host "  $name" }
if ($removable.Count -gt 0) {
    Write-Host ''
    Write-Host 'not reachable from the seeds:'
    foreach ($r in $removable) { Write-Host "  $r" }
}

if ($OutDir) {
    $null = New-Item -ItemType Directory -Force -Path $OutDir
    Set-Content -Encoding utf8 (Join-Path $OutDir 'closure.txt') $kept
    Set-Content -Encoding utf8 (Join-Path $OutDir 'removable.txt') $removable
    Set-Content -Encoding utf8 (Join-Path $OutDir 'external.txt') @($external.Keys | Sort-Object)
    $edges | Sort-Object From, To | Export-Csv -NoTypeInformation -Encoding utf8 (Join-Path $OutDir 'edges.csv')
    Write-Host ''
    Write-Host "Written to $OutDir"
}
