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
      * the libraries named from managed code: ImportResolver's libgstreamer-1.0-0.dll,
        libgstvideo-1.0-0.dll and libgobject-2.0-0.dll, plus GStreamerRuntimeLocator's
        libglib-2.0-0.dll
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

.PARAMETER SeedPlugins
    File names of the plugins to seed with. Defaults to every plugin in the tree.

.PARAMETER OutDir
    Write closure.txt / removable.txt / external.txt / edges.csv here.

.EXAMPLE
    tools\Get-GStreamerImportClosure.ps1 -RuntimeRoot src\GStreamer.GstSharpNet\runtimes\win-x64

.OUTPUTS
    A summary on stdout. Exit code is 0 even when files are removable -- this reports, it
    does not delete.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RuntimeRoot,
    [string] $Objdump = 'objdump.exe',
    [string[]] $SeedPlugins,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RuntimeRoot = (Resolve-Path $RuntimeRoot).Path.TrimEnd('\')
$objdumpCommand = Get-Command $Objdump -ErrorAction SilentlyContinue
if (-not $objdumpCommand) {
    throw "objdump not found: '$Objdump'. Install MSYS2 binutils and pass -Objdump <path to objdump.exe>."
}
$ObjdumpExe = $objdumpCommand.Source

# Run objdump through System.Diagnostics.Process, NOT the PowerShell pipeline with a
# stderr redirect. In Windows PowerShell 5.1, redirecting a native command's stderr
# (2>$null included) wraps every line in an ErrorRecord, and with
# $ErrorActionPreference='Stop' the first warning objdump prints would terminate the
# whole walk (.claude/rules/powershell.md bans 2>&1 for the same mechanism).
# Also fail loudly when objdump cannot parse a file: a silently-empty import list makes
# the closure smaller, and the closure is what decides which files may be DELETED from
# the bundled runtime -- the exact failure mode this script's own docs call the top risk.
function Get-ImportedDllNames {
    param([string] $Path)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $ObjdumpExe
    $psi.Arguments              = '-p "' + $Path + '"'
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
        throw "objdump failed for '$Path' (exit $($p.ExitCode)): $($errText.Trim())"
    }
    return @($out -split "`r?`n" | ForEach-Object {
        if ($_ -match '^\s*DLL Name:\s*(\S+)\s*$') { $Matches[1] }
    })
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
foreach ($s in $namedSeeds) {
    if (Test-Path (Join-Path $RuntimeRoot ($s -replace '/', '\'))) { $null = $seeds.Add($s) }
    else { Write-Warning "seed not in tree: $s" }
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
