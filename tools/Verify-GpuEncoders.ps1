<#
.SYNOPSIS
    Unattended verification of the H.264 encoder selection (EncoderCatalog) on a
    machine with a real GPU.

.DESCRIPTION
    Everything the GPU check needs is done by this script: it creates an isolated data
    directory, generates settings.json for each case, starts the resident worker,
    records, validates the produced MP4 by parsing the ISO-BMFF container directly, and
    writes a report. No manual steps, no hand-edited settings, no GUI interaction.

    Cases are derived from the machine itself: the script asks gst-inspect which H.264
    encoders exist and runs one case per available encoder, so the same command does the
    right thing on Intel, NVIDIA, AMD and GPU-less boxes.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

.PARAMETER PublishDir
    The published application directory (output of
    'dotnet publish -p:PublishProfile=win-x64-aot' -- the shipped form is Native AOT).
    Defaults to the repo's AOT publish output relative to this script.

.PARAMETER WorkDir
    Where to put the isolated data directory and the report.
    Defaults to a new folder under %TEMP%.

.PARAMETER KeepWorkDir
    Keep the working directory (settings.json, debug.log, MP4s) after the run.

.EXAMPLE
    .\Verify-GpuEncoders.ps1
    .\Verify-GpuEncoders.ps1 -PublishDir D:\pra\publish -KeepWorkDir

.OUTPUTS
    A markdown report at <WorkDir>\gpu-encoder-report.md and the same summary on stdout.
    Exit code 0 only if every case actually ran AND behaved as expected; 1 otherwise
    (a skipped case verified nothing, so it counts as inconclusive, not as success).
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$WorkDir,
    [int]$RecordSeconds = 3,
    [string]$GstDebug = '',
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'

# The caller's PATH, captured before anything touches it. Restored at the end and on the way
# out of an error -- see the comment at the point where GStreamer's bin is prepended.
$script:CallerPath = $env:PATH

function Restore-CallerPath {
    if ($null -ne $script:CallerPath) { $env:PATH = $script:CallerPath }
}

# `break` rethrows, so this only adds the cleanup; it does not swallow the failure.
# Also stop this run's workers: an abnormal exit would otherwise leave a resident
# worker running against the (now abandoned) temp profile until the machine reboots.
# Stop-AllWorkers is defined further down, so guard for traps that fire before that.
trap {
    # Restore the PATH FIRST: a failure inside this trap propagates outwards without
    # re-entering it, so anything after the failing statement would be skipped -- and the
    # PATH restore is the one guarantee this trap has always owned.
    Restore-CallerPath
    # Also stop this run's workers: an abnormal exit would otherwise leave a resident
    # worker running against the (now abandoned) temp profile until the machine reboots.
    # Guard on $WorkDir too -- the helper reads it, and a trap can fire before setup.
    if ($WorkDir -and (Test-Path function:Stop-AllWorkers)) { Stop-AllWorkers }
    break
}

# ---------------------------------------------------------------- setup

if (-not $PublishDir) {
    $PublishDir = Join-Path $PSScriptRoot '..\src\ProcessRecorderApp\bin\Release\win-x64\publish\aot'
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
$exe = Join-Path $PublishDir 'ProcessRecorderApp.exe'
if (-not (Test-Path $exe)) {
    throw "ProcessRecorderApp.exe not found under '$PublishDir'. Run 'dotnet publish -p:PublishProfile=win-x64-aot' first, or pass -PublishDir."
}

# GStreamer is not assumed to sit next to the exe: a published app may be using an
# installed GStreamer (MSVC) or the bundled copy under the publish directory. Approximate
# the order the app itself uses (GStreamerRuntimeLocator: PATH -> MSVC env var ->
# registry (MSVC install) -> bundled gstreamer\win-x64\bin); any mismatch is detected at
# run time by Verify-BinMatchesTheAppsChoice. The DllImports are MSVC-named
# (gstreamer-1.0-0.dll), so MinGW installs can never satisfy them and are not probed here.
function Find-GStreamerBin {
    param([string] $PublishDir)

    $candidates = @()

    # PATH FIRST -- this is the app's highest-priority candidate (GStreamerRuntimeLocator.Select
    # walks PATH in order and takes the first directory holding gstreamer-1.0-0.dll).
    #
    # Skipping PATH here would have two consequences, and the second is the dangerous one:
    #   1. A GStreamer that is only on PATH would be reported as "not found".
    #   2. On a machine that has one on PATH AND one elsewhere, the script would inspect a
    #      DIFFERENT GStreamer than the app loads -- so "encoders present on this machine"
    #      and every per-case expectation would describe a runtime that was never under test.
    # Verify-BinMatchesTheAppsChoice below checks 2 at run time instead of trusting this.
    foreach ($dir in ($env:PATH -split ';')) {
        if ($dir) { $candidates += $dir.Trim() }
    }

    $envRoot = $env:GSTREAMER_1_0_ROOT_MSVC_X86_64
    if ($envRoot) { $candidates += (Join-Path $envRoot 'bin') }

    # A per-user MSVC install may set neither the environment variable nor PATH,
    # so look the install up in the uninstall registry the way the app does.
    $uninstall = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
    foreach ($pattern in $uninstall) {
        $entries = @(Get-ItemProperty $pattern -ErrorAction SilentlyContinue |
                     Where-Object { $_.PSObject.Properties['DisplayName'] -and
                                    $_.DisplayName -like 'GStreamer*' -and $_.DisplayName -like '*MSVC*' -and
                                    $_.PSObject.Properties['InstallLocation'] -and $_.InstallLocation })
        foreach ($e in $entries) { $candidates += (Join-Path $e.InstallLocation.TrimEnd('\', '/') 'bin') }
    }

    $candidates += (Join-Path $PublishDir 'gstreamer\win-x64\bin')

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'gstreamer-1.0-0.dll')) { return $c }
    }
    return $null
}

$gstBin     = Find-GStreamerBin -PublishDir $PublishDir
$gstInspect = if ($gstBin) { Join-Path $gstBin 'gst-inspect-1.0.exe' } else { $null }

if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pra-gpu-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$null = New-Item -ItemType Directory -Force $WorkDir

# Isolate completely from any resident instance the developer may have running on this
# machine: without BOTH variables the test would forward its commands to that instance
# (and its recordings would land in the real user profile).
$env:PROCESSRECORDERAPP_DATA_DIR   = $WorkDir
$env:PROCESSRECORDERAPP_KEY_PREFIX = 'PraGpuVerify_' + [guid]::NewGuid().ToString('N')

$recDir = Join-Path $WorkDir 'rec'
$null = New-Item -ItemType Directory -Force $recDir

# ---------------------------------------------------------------- helpers

# Kill only the workers that belong to THIS run. They are identified by the pid each
# worker writes to the isolated activity.log ('app.start pid=N') under $WorkDir -- the
# same discipline the C# harness uses (AppInstance.KillWorkers). Matching by process
# name would also kill a real resident instance the user keeps running on this machine,
# which is exactly what the DATA_DIR/KEY_PREFIX isolation above promises not to do
# (an in-progress recording of that instance would be lost unfinalized).
function Stop-AllWorkers {
    $workerIds = @()
    foreach ($log in @((Join-Path $WorkDir 'activity.log'), (Join-Path $WorkDir 'activity.log.1'))) {
        if (Test-Path $log) {
            $found = Select-String -Path $log -Pattern 'app\.start pid=(\d+)' -AllMatches -ErrorAction SilentlyContinue
            foreach ($m in @($found | ForEach-Object { $_.Matches })) {
                $workerIds += [int]$m.Groups[1].Value
            }
        }
    }
    foreach ($workerId in @($workerIds | Sort-Object -Unique)) {
        $p = Get-Process -Id $workerId -ErrorAction SilentlyContinue
        if ($null -ne $p -and $p.ProcessName -eq 'ProcessRecorderApp') {
            try { Stop-Process -Id $workerId -Force -ErrorAction Stop } catch { }
        }
    }
    Start-Sleep -Milliseconds 600
}

# NOTE: never use Start-Process -Wait here. It waits for the whole process tree, and the
# resident worker never exits, so it would hang forever.
function Invoke-Cli {
    param([string]$Arguments, [int]$TimeoutMs = 90000)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $exe
    $psi.Arguments              = $Arguments
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    $p = [System.Diagnostics.Process]::Start($psi)

    # Read both pipes asynchronously BEFORE waiting. A synchronous ReadToEnd
    # (1) never returns while a hung CLI keeps stdout open, so the timeout below
    #     would never fire and an unattended run would hang forever, and
    # (2) deadlocks outright once the process fills the other pipe's buffer
    #     (the same trap the C# harness documents in AppInstance.Run).
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()

    $timedOut = -not $p.WaitForExit($TimeoutMs)
    if ($timedOut) {
        try { $p.Kill() } catch { }
        # Kill is asynchronous; give the pipes a moment to break so the reads finish.
        $null = $p.WaitForExit(5000)
    }

    $out = ''
    $err = ''
    try { if ($outTask.Wait(2000)) { $out = $outTask.Result } } catch { }
    try { if ($errTask.Wait(2000)) { $err = $errTask.Result } } catch { }

    # After Kill() the exit code may be unreadable; report the timeout itself as the
    # failure instead of aborting the whole script on $p.ExitCode.
    $exit = if ($timedOut) { -1 } else { $p.ExitCode }

    return [pscustomobject]@{
        ExitCode = $exit
        StdOut   = $out.Trim()
        StdErr   = $err.Trim()
    }
}

function Write-Settings {
    param(
        [string]$RecorderType,          # 'System' | 'D3d12'
        [string]$Preferred = '',
        [string]$EncodingProperties,    # $null => automatic selection
        [string]$GstDebug = ''          # e.g. '4' to capture GStreamer diagnostics
    )

    # 320x240@15 on purpose: on a GPU-less box the D3D12 path runs on the WARP software
    # rasterizer, and a heavier source simply cannot keep up (a 3s recording then yields
    # well under a second of video). This script is about which encoder gets selected and
    # whether it produces a valid MP4, not about throughput.
    $src = if ($RecorderType -eq 'D3d12') {
        'd3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=320, height=240, framerate=15/1'
    } else {
        'videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420,width=320,height=240,framerate=15/1'
    }

    $encProp = if ([string]::IsNullOrEmpty($EncodingProperties)) { 'null' } else { '"' + $EncodingProperties + '"' }
    $tmpl    = ($recDir -replace '\\', '\\') + '\\{Name}_{Now:HHmmssfff}.mp4'
    $logPath = ($WorkDir -replace '\\', '\\') + '\\debug.log'

    $json = @"
{
  "DataVersion": 1,
  "DebugLogFile": "$logPath",
  "GstDebug": "$GstDebug",
  "PreferredH264Encoder": "$Preferred",
  "Recorders": [
    {
      "Name": "R1",
      "BufferDuration": 10000,
      "FilenameTemplate": "$tmpl",
      "Type": "$RecorderType",
      "SrcPipeline": "$src",
      "EncodingProperties": $encProp
    }
  ]
}
"@
    Set-Content -Path (Join-Path $WorkDir 'settings.json') -Value $json -Encoding utf8
}

# Minimal ISO-BMFF probe. Deliberately dependency-free: gst-discoverer needs a working
# plugin path and an extra process, and this only has to answer "is it a real MP4 with an
# H.264 track, and how long is it".
function Test-Mp4 {
    param([string]$Path)

    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $res = [pscustomobject]@{
            HasFtyp = $false; HasMoov = $false; HasMdat = $false
            HasAvc1 = $false; DurationSec = $null
        }

        while ($fs.Position -lt $fs.Length - 8) {
            $start = $fs.Position
            $b = $br.ReadBytes(4)
            if ($b.Length -lt 4) { break }
            $size = ([uint32]$b[0] -shl 24) -bor ([uint32]$b[1] -shl 16) -bor ([uint32]$b[2] -shl 8) -bor [uint32]$b[3]
            $type = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
            if ($size -eq 1) { $size = [int64]$br.ReadUInt64() }
            if ($size -lt 8) { break }

            switch ($type) {
                'ftyp' { $res.HasFtyp = $true }
                'mdat' { $res.HasMdat = $true }
                'moov' {
                    $res.HasMoov = $true
                    $csize = $br.ReadBytes(4)
                    $ctype = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
                    if ($ctype -eq 'mvhd') {
                        $ver = $br.ReadByte()
                        $null = $br.ReadBytes(3)
                        if ($ver -eq 1) {
                            $null = $br.ReadBytes(16)
                            $tsb = $br.ReadBytes(4)
                            $timescale = ([uint32]$tsb[0] -shl 24) -bor ([uint32]$tsb[1] -shl 16) -bor ([uint32]$tsb[2] -shl 8) -bor [uint32]$tsb[3]
                            $db = $br.ReadBytes(8); [array]::Reverse($db)
                            $duration = [System.BitConverter]::ToUInt64($db, 0)
                        } else {
                            $null = $br.ReadBytes(8)
                            $tsb = $br.ReadBytes(4)
                            $timescale = ([uint32]$tsb[0] -shl 24) -bor ([uint32]$tsb[1] -shl 16) -bor ([uint32]$tsb[2] -shl 8) -bor [uint32]$tsb[3]
                            $db = $br.ReadBytes(4)
                            $duration = ([uint32]$db[0] -shl 24) -bor ([uint32]$db[1] -shl 16) -bor ([uint32]$db[2] -shl 8) -bor [uint32]$db[3]
                        }
                        if ($timescale -gt 0) { $res.DurationSec = [math]::Round($duration / $timescale, 3) }
                    }
                    $fs.Position = $start + 8
                    $moov = $br.ReadBytes([int]($size - 8))
                    $res.HasAvc1 = [System.Text.Encoding]::ASCII.GetString($moov).Contains('avcC')
                }
            }
            $fs.Position = $start + $size
        }
        return $res
    } finally { $fs.Dispose() }
}

function Get-EncoderLogLines {
    $log = Join-Path $WorkDir 'debug.log'
    if (-not (Test-Path $log)) { return @() }
    return @(Get-Content $log | Where-Object { $_ -match 'gst\.encoder' })
}

# Does the GStreamer this script inspected match the one the app actually loaded?
#
# The app writes the directory it resolved to activity.log (gst.runtime ... dir=...), so this
# does not have to be inferred -- it can be read. Checking it matters because the whole report
# is built from gst-inspect against $gstBin: if the app loads a different runtime, every
# "present / absent" line describes something that was never under test, and the report looks
# authoritative while being about the wrong machine state.
# The value of a field CAN CONTAIN SPACES: the default install location is
# 'C:\Program Files\gstreamer\1.0\mingw_x86_64\bin'. Reading it as \S+ stops at the first
# space and yields 'C:\Program', which then never equals what this script inspected --
# so the script reports a MISMATCH that is not real, on the most ordinary install there
# is, and every warning it prints that way is false.
#
# So: read up to the next 'name=' token instead. This is the same rule the L2 test uses
# (RuntimeResolutionTests.Field) -- the line is 'selected=.. dir=.. core=.. glib=.. mixed=..'.
# GpuVerifyScriptParsingTests (L1) pulls this very expression out of this file and runs it
# against a path with spaces -- PowerShell and .NET share the regex engine, so the rule is
# not written down twice.
$script:GstRuntimeFieldPattern = '\bdir=(.*?)(?=\s+\w[\w:.]*=|$)'

function Get-AppResolvedGstDir {
    $log = Join-Path $WorkDir 'activity.log'
    if (-not (Test-Path $log)) { return $null }

    $line = @(Get-Content $log | Where-Object { $_ -match 'gst\.runtime' }) | Select-Object -First 1
    if (-not $line) { return $null }
    if ($line -notmatch $script:GstRuntimeFieldPattern) { return $null }

    $dir = $matches[1].Trim()

    # A value that is not a directory means the line was parsed wrong, not that the app
    # loaded something odd. Say which one it is -- a wrong "MISMATCH" sends the reader
    # after the search order, which is the one place the answer is not.
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        Write-Warning "Could not read dir= out of the gst.runtime line (got '$dir'). The runtime cross-check below is skipped."
        Write-Warning "  line: $line"
        return $null
    }
    return $dir
}

function Verify-BinMatchesTheAppsChoice {
    param([string] $GstBin)

    $appDir = Get-AppResolvedGstDir
    if (-not $appDir) { return }

    if (-not $GstBin) {
        Write-Warning "The app resolved GStreamer to '$appDir', but this script found none. The report below is not about that runtime."
        return
    }

    $a = [IO.Path]::GetFullPath($appDir).TrimEnd('\')
    $b = [IO.Path]::GetFullPath($GstBin).TrimEnd('\')
    if ($a -ieq $b) { return }

    Write-Warning "MISMATCH: this script inspected '$b' but the app loaded '$a'."
    Write-Warning "Every 'present / absent' line below describes the wrong runtime. Fix the search order before trusting the report."
}

# Did the publish directory under test actually supply the runtime?
#
# The check above compares "what this script inspected" with "what the app loaded". Both can
# agree and still be the wrong thing: if a runtime from some OTHER directory is ahead on PATH,
# both point at it and no warning fires -- while the bundled runtime of the artefact you are
# verifying is never touched.
#
# So confirm at run time that the artefact under test is really the one that supplied the
# runtime, and say so in the report when it is not -- otherwise a stale runtime left over
# on PATH goes unmentioned and has to be noticed by eye in the header.
#
# This is a warning, not a failure: running a non-bundled publish against a runtime placed on
# PATH is a deliberate, supported mode.
function Test-BundledRuntimeWasExercised {
    param([string] $PublishDirectory, [switch] $Quiet)

    $bundled = Join-Path $PublishDirectory 'gstreamer\win-x64\bin'
    if (-not (Test-Path (Join-Path $bundled 'gstreamer-1.0-0.dll'))) {
        # Non-bundled publish: it is supposed to resolve GStreamer from somewhere else.
        return $null
    }

    $appDir = Get-AppResolvedGstDir
    if (-not $appDir) { return $null }

    $a = [IO.Path]::GetFullPath($appDir).TrimEnd('\')
    $b = [IO.Path]::GetFullPath($bundled).TrimEnd('\')
    if ($a -ieq $b) { return $null }

    if (-not $Quiet) {
        Write-Warning "The publish dir ships a bundled runtime, but the app loaded GStreamer from '$a'."
        Write-Warning "The bundled runtime under '$b' was NOT exercised. Usually something else is ahead on PATH."
    }
    return $a
}

# ---------------------------------------------------------------- probe the machine

Write-Host "Publish dir : $PublishDir"
Write-Host "Work dir    : $WorkDir"
Write-Host ''

# Must stay identical to EncoderCatalog's factory names. When it drifts, this script asks
# gst-inspect about a name that does not exist and the real element is never probed, never
# gets a case, and never appears in the report -- silently: an encoder the machine HAS is
# reported as absent and the item is wrongly recorded as unverifiable.
# EncoderCatalogScriptSyncTests (L1) pins this list to the catalog.
$allEncoders = @('d3d12h264enc', 'qsvh264enc', 'nvd3d11h264enc', 'nvh264enc', 'nvautogpuh264enc',
                 'amfh264enc', 'mfh264enc', 'openh264enc', 'x264enc')
$present = @()

# Machine truth, independent of the list above: ask gst-inspect what H.264 encoders this
# box actually has. A targeted probe can only ever confirm names we already thought of --
# it cannot tell us we are asking the wrong question. Anything here that is missing from
# $allEncoders is a catalog gap, and is reported instead of silently ignored.
function Get-AllH264EncodersOnThisMachine {
    param([string]$GstInspect)

    if (-not $GstInspect -or -not (Test-Path $GstInspect)) { return @() }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $GstInspect
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    # Async reads before waiting: a synchronous ReadToEnd would defeat the timeout and
    # can deadlock when the other pipe's buffer fills (see Invoke-Cli).
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { }; $null = $p.WaitForExit(5000) }
    $out = ''
    try { if ($outTask.Wait(2000)) { $out = $outTask.Result } } catch { }
    try { $null = $errTask.Wait(2000) } catch { }

    # Lines look like "nvcodec:  nvd3d11h264enc: NVENC H.264 Video Encoder D3D11 Mode"
    #
    # Match on '264enc', NOT 'h264enc'. x264enc has no 'h' in it, so the obvious filter
    # drops the single most common software encoder without saying anything -- the exact
    # failure mode this enumeration exists to catch, reintroduced one level down.
    # EncoderCatalogScriptSyncTests checks this pattern against every catalog name.
    $found = @()
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*([A-Za-z0-9_\-]+):\s+([A-Za-z0-9_]*264enc[A-Za-z0-9_]*):') {
            $found += $matches[2]
        }
    }
    return @($found | Sort-Object -Unique)
}
Write-Host ("GStreamer bin : " + $(if ($gstBin) { $gstBin } else { '(not found)' }))
if ($gstInspect -and (Test-Path $gstInspect)) {
    # **This leaks into the caller's console, so it has to be undone.**
    #
    # A .ps1 runs INSIDE the calling PowerShell process (unlike `powershell -File`, which gets
    # its own), so `$env:PATH = ...` here survives until that window is closed -- and every
    # process started from it afterwards inherits it, including the app under test.
    #
    # Verifying two bundles back to back in the same console would otherwise leave the
    # first one's runtime at the front of PATH, so the second run would load the first
    # one's GStreamer -- and the report would be about a runtime that was never under test.
    #
    # Nothing here is persistent: neither this script nor the app ever writes to
    # HKCU\Environment or HKLM (the app uses Environment.SetEnvironmentVariable's one-argument
    # overload, which is process-scoped). Closing the window was always enough -- the point of
    # restoring is that you should not have to know that.
    $env:PATH = "$gstBin;$env:PATH"
    foreach ($e in $allEncoders) {
        # Run gst-inspect through System.Diagnostics.Process rather than the PowerShell
        # call operator: in Windows PowerShell 5.1 a native command writing to stderr is
        # turned into a NativeCommandError, which would abort this loop for every element
        # that legitimately does not exist on this machine.
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName               = $gstInspect
        $psi.Arguments              = $e
        $psi.UseShellExecute        = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError  = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        # Async reads before waiting (see Invoke-Cli): the output is discarded but the
        # pipes still fill up, and a synchronous ReadToEnd would defeat the timeout.
        $outTask = $p.StandardOutput.ReadToEndAsync()
        $errTask = $p.StandardError.ReadToEndAsync()
        $timedOut = -not $p.WaitForExit(30000)
        if ($timedOut) { try { $p.Kill() } catch { }; $null = $p.WaitForExit(5000) }
        try { $null = $outTask.Wait(2000) } catch { }
        try { $null = $errTask.Wait(2000) } catch { }
        if (-not $timedOut -and $p.ExitCode -eq 0) { $present += $e }
    }
} else {
    Write-Warning "gst-inspect-1.0.exe not found; cases will be derived from the app's own probe instead."
}

# Kept in sync with EncoderCatalog: D3d12Candidates minus SystemCandidates.
$gpuEncoders = @($present | Where-Object { $_ -in @('d3d12h264enc', 'qsvh264enc', 'nvd3d11h264enc', 'nvh264enc', 'nvautogpuh264enc', 'amfh264enc') })

$onMachine = @(Get-AllH264EncodersOnThisMachine -GstInspect $gstInspect)
$unknown   = @($onMachine | Where-Object { $_ -notin $allEncoders })

Write-Host ("H.264 encoders present on this machine : " + ($present -join ', '))
Write-Host ("GPU encoders among them                : " + $(if ($gpuEncoders) { $gpuEncoders -join ', ' } else { '(none -- GPU cases will be reported as N/A)' }))
if ($onMachine.Count -gt 0) {
    Write-Host ("All H.264 encoders gst-inspect knows   : " + ($onMachine -join ', '))
}
if ($unknown.Count -gt 0) {
    Write-Host ''
    Write-Warning ("This machine has H.264 encoders the catalog does not know about: " + ($unknown -join ', '))
    Write-Warning "They get no case here and are never tried by the app. Add them to EncoderCatalog."
}
Write-Host ''

# ---------------------------------------------------------------- build the case list

$cases = New-Object System.Collections.Generic.List[object]

$cases.Add([pscustomobject]@{
    Name = 'D3d12 / automatic selection'; Type = 'D3d12'; Preferred = ''; Enc = $null
    Expect = 'records'; Note = 'the core contract: D3d12 must record on whatever GPU is present'
})
$cases.Add([pscustomobject]@{
    Name = 'System / automatic selection'; Type = 'System'; Preferred = ''; Enc = $null
    Expect = 'records'; Note = 'the configuration CI uses'
})
foreach ($g in $gpuEncoders) {
    $cases.Add([pscustomobject]@{
        Name = "D3d12 / PreferredH264Encoder=$g"; Type = 'D3d12'; Preferred = $g; Enc = $null
        Expect = 'records'; Note = "GPU encoder present on this machine; must be selected and produce a valid MP4"
        MustSelect = $g
    })
}
$cases.Add([pscustomobject]@{
    Name = 'D3d12 / PreferredH264Encoder=nosuchh264enc'; Type = 'D3d12'; Preferred = 'nosuchh264enc'; Enc = $null
    Expect = 'records'; Note = 'a preferred encoder that does not exist must fall through, not break recording'
})
# Manual override of a *system-memory* encoder on the D3D12 path (the case exists to prove
# d3d12download is inserted). The encoder must be chosen from what this machine actually
# has -- never hard-code one. The bundled runtime deliberately ships no GPL encoder, so
# e.g. x264enc does not exist there, and the product is right to refuse it (an explicit
# EncodingProperties naming a missing element must fail loudly, not silently substitute) --
# so a hard-coded name turns an intentionally absent element into a FAILED row that looks
# like a product defect.
#
# Never assume an element is present. Ask $present, and if none of the candidates is there,
# say the case was skipped and why -- a skipped case that announces itself is honest, a
# FAILED row is a false alarm.
# The list must stay identical to EncoderCatalog's SystemCandidates -- names, strings and
# order. Pinned by EncoderCatalogScriptSyncTests (L1), which compares the whole list against
# the catalog: a missing candidate silently un-covers d3d12download on machines where only
# that candidate exists (a skip is not a failure), and a stale property string makes the
# case fail on real hardware and look like a product defect.
$manualCandidates = @(
    [pscustomobject]@{ Name = 'x264enc';     Props = 'x264enc tune=zerolatency bitrate=2000 speed-preset=ultrafast key-int-max=60' }
    [pscustomobject]@{ Name = 'openh264enc'; Props = 'openh264enc bitrate=2000000 gop-size=60' }
    [pscustomobject]@{ Name = 'mfh264enc';   Props = 'mfh264enc bitrate=2000 gop-size=60 low-latency=true' }
)
$manual = @($manualCandidates | Where-Object { $_.Name -in $present })[0]
if ($manual) {
    $cases.Add([pscustomobject]@{
        Name = "D3d12 / manual EncodingProperties=$($manual.Name)"; Type = 'D3d12'; Preferred = ''
        Enc = $manual.Props
        Expect = 'records'; Note = 'manual override of a system-memory encoder on the D3D12 path (needs d3d12download)'
        MustSelect = $manual.Name
    })
} else {
    Write-Warning ("Skipping the manual system-memory override case: none of " +
        (($manualCandidates | ForEach-Object { $_.Name }) -join ', ') +
        " is present. The bundled runtime ships no GPL encoder, so this is expected there --" +
        " it means d3d12download is NOT covered by this run.")
}

# ---------------------------------------------------------------- run

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param([object]$Case, [string]$GstDebug = '')

    Stop-AllWorkers
    Get-ChildItem $recDir -Filter *.mp4 -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path $WorkDir 'debug.log') -Force -ErrorAction SilentlyContinue

    Write-Settings -RecorderType $Case.Type -Preferred $Case.Preferred `
                   -EncodingProperties $Case.Enc -GstDebug $GstDebug

    # The very first launch on a machine builds the GStreamer plugin registry, which can
    # take longer than the launcher's wait and yields exit code 1 (ExitCode_WorkerStartFailed:
    # the wait for the worker's registration timed out). Retry once.
    $ping = Invoke-Cli 'ping'
    if ($ping.ExitCode -ne 0) {
        Write-Host "   (first launch timed out building the plugin registry; retrying)"
        Start-Sleep -Seconds 3
        $ping = Invoke-Cli 'ping'
    }
    # A slow machine can still be building the registry after the second attempt. Give it one
    # long wait before giving up -- a SKIPPED case verifies nothing, so it is worth the minute.
    if ($ping.ExitCode -ne 0) {
        Write-Host "   (still not up; waiting 30s for the plugin registry, then retrying once more)"
        Start-Sleep -Seconds 30
        $ping = Invoke-Cli 'ping'
    }

    # If the worker never answered, the case never ran: report it as a harness/startup
    # failure, NOT as a product FAILED. A FAILED row in the report reads as "this encoder
    # is broken on this machine" (a false red is worse than a real one -- it leaves a
    # wrong record about the product), while the real story here is that the app did not
    # start at all (ping stderr has the launcher's reason).
    if ($ping.ExitCode -ne 0) {
        return [pscustomobject]@{
            Case           = $Case.Name
            Note           = $Case.Note
            StartExit      = $null
            StopExit       = $null
            Selected       = $null
            FailedAttempts = $null
            Mp4Bytes       = 0
            DurationSec    = $null
            ValidMp4       = $false
            Ok             = $false
            Skipped        = "worker did not start (ping exit $($ping.ExitCode))"
            Shortfall      = $null
            StartStdErr    = $ping.StdErr
            LogLines       = ''
            Diagnostics    = $null
        }
    }

    # Let the ring buffer fill before recording, so the produced file also demonstrates
    # the pre-buffer (BufferDuration=2000).
    Start-Sleep -Seconds 4

    $start = Invoke-Cli 'start-recording-all'
    Start-Sleep -Seconds $RecordSeconds
    $stop = Invoke-Cli 'stop-recording-all'
    Start-Sleep -Seconds 2

    Stop-AllWorkers   # flush the debug log writer

    $logLines = Get-EncoderLogLines
    $selected = ($logLines | Where-Object { $_ -match 'gst\.encoder selected' } |
                 ForEach-Object { if ($_ -match "encoder='([^']*)'") { $matches[1] } } | Select-Object -Last 1)
    $failedAttempts = ($logLines | Where-Object { $_ -match 'gst\.encoder selected' } |
                 ForEach-Object { if ($_ -match 'failedAttempts=(\d+)') { [int]$matches[1] } } | Select-Object -Last 1)

    $mp4 = Get-ChildItem $recDir -Filter *.mp4 -ErrorAction SilentlyContinue | Select-Object -First 1
    $probe = if ($mp4) { Test-Mp4 $mp4.FullName } else { $null }

    $ok = ($start.ExitCode -eq 0) -and ($stop.ExitCode -eq 0) -and
          ($null -ne $probe) -and $probe.HasFtyp -and $probe.HasMoov -and $probe.HasMdat -and
          $probe.HasAvc1 -and ($probe.DurationSec -gt 0)

    if ($Case.MustSelect -and $selected -and ($selected -notlike "$($Case.MustSelect)*")) {
        $ok = $false
    }

    # A file shorter than the recording window is almost always the GOP length, NOT frame
    # dropping: recording only begins at the first I-frame, so if the ring buffer
    # (BufferDuration) holds no keyframe, the whole pre-buffer is discarded and live frames
    # keep being discarded until the next one arrives. Check the encoder's gop-size /
    # key-int-max against BufferDuration before suspecting anything else.
    # Note this metric is inherently noisy -- the same config can differ by seconds
    # depending on where the GOP boundary happens to fall. Do not gate on it.
    $shortfall = $null
    if ($probe -and $null -ne $probe.DurationSec -and $probe.DurationSec -lt $RecordSeconds) {
        $shortfall = "captured $($probe.DurationSec)s of a ${RecordSeconds}s window"
    }

    return [pscustomobject]@{
        Case           = $Case.Name
        Note           = $Case.Note
        StartExit      = $start.ExitCode
        StopExit       = $stop.ExitCode
        Selected       = $selected
        FailedAttempts = $failedAttempts
        Mp4Bytes       = if ($mp4) { $mp4.Length } else { 0 }
        DurationSec    = if ($probe) { $probe.DurationSec } else { $null }
        ValidMp4       = if ($probe) { $probe.HasFtyp -and $probe.HasMoov -and $probe.HasMdat -and $probe.HasAvc1 } else { $false }
        Ok             = $ok
        Skipped        = $null
        Shortfall      = $shortfall
        StartStdErr    = $start.StdErr
        LogLines       = ($logLines -join "`n")
        Diagnostics    = $null
    }
}

foreach ($case in $cases) {
    Write-Host "== $($case.Name)"
    $r = Invoke-Case -Case $case -GstDebug $GstDebug

    Write-Host ("   exit start/stop = {0}/{1}   selected = {2}   duration = {3}s   -> {4}" -f `
        $r.StartExit, $r.StopExit, $r.Selected, $(if ($null -ne $r.DurationSec) { $r.DurationSec } else { 'n/a' }),
        $(if ($r.Ok) { 'OK' } elseif ($r.Skipped) { "SKIPPED ($($r.Skipped))" } else { 'FAILED' }))
    if ($r.Shortfall) { Write-Host "   NOTE: $($r.Shortfall) -- check the encoder's GOP length vs BufferDuration" -ForegroundColor Yellow }
    if (-not $r.Ok -and $r.StartStdErr) { Write-Host "   stderr: $($r.StartStdErr)" }

    # A case that initialises cleanly (failedAttempts=0, exit 0) yet produces an invalid
    # MP4 has failed ASYNCHRONOUSLY -- a class of failure ParseLaunch and SetState(Playing)
    # structurally cannot catch. Re-run it once with GStreamer diagnostics so the cause is
    # in the report and no second trip to this machine is needed.
    # (A SKIPPED case never ran, so there is nothing to diagnose with GST_DEBUG.)
    if (-not $r.Ok -and -not $r.Skipped) {
        Write-Host "   re-running this case with GST_DEBUG to capture the cause..." -ForegroundColor Yellow
        $d = Invoke-Case -Case $case -GstDebug '4'
        $log = Join-Path $WorkDir 'debug.log'
        $diag = @()
        if (Test-Path $log) {
            $diag = @(Get-Content $log |
                      Where-Object { $_ -match 'ERROR|WARN|error|not-negotiated|Internal data stream|failed' } |
                      Select-Object -First 60)
        }
        $r.Diagnostics = ($diag -join "`n")
        Write-Host ("   captured {0} diagnostic line(s)" -f $diag.Count)
    }

    $results.Add($r)
    Write-Host ''
}

Stop-AllWorkers

# ---------------------------------------------------------------- report

$reportPath = Join-Path $WorkDir 'gpu-encoder-report.md'
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('# GPU encoder verification report')
$null = $sb.AppendLine()
$null = $sb.AppendLine("- Machine: $env:COMPUTERNAME")
$null = $sb.AppendLine("- Publish dir: ``$PublishDir``")

# The runtime the app actually loaded belongs in the report, not only on the console.
# The report is what gets pasted back and read later, and this is the line that tells you
# whether the report is about the artefact you think it is (a stale runtime ahead on PATH
# would otherwise show up only in the console header).
$appGstDir = Get-AppResolvedGstDir
$null = $sb.AppendLine("- GStreamer runtime the app loaded: ``$(if ($appGstDir) { $appGstDir } else { '(not recorded)' })``")
$notExercised = Test-BundledRuntimeWasExercised -PublishDirectory $PublishDir -Quiet
if ($notExercised) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("**The bundled runtime in the publish dir was NOT exercised.** The app loaded " +
        "``$notExercised`` instead (something else is ahead on PATH). The results below are about the " +
        'app assemblies of this publish dir combined with that other runtime.')
    $null = $sb.AppendLine()
}
$null = $sb.AppendLine("- H.264 encoders present: ``$($present -join ', ')``")
$null = $sb.AppendLine("- GPU encoders present: ``$(if ($gpuEncoders) { $gpuEncoders -join ', ' } else { '(none)' })``")
$null = $sb.AppendLine("- All H.264 encoders gst-inspect reports: ``$(if ($onMachine) { $onMachine -join ', ' } else { '(could not enumerate)' })``")
if ($unknown.Count -gt 0) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("**This machine has H.264 encoders the catalog does not know about: ``$($unknown -join ', ')``.**")
    $null = $sb.AppendLine('They get no case here, and the app will never try them. Add them to EncoderCatalog.')
}
$null = $sb.AppendLine()
$null = $sb.AppendLine("- Recording window per case: ${RecordSeconds}s (BufferDuration=10000ms, 4s pre-roll)")
$null = $sb.AppendLine()
$null = $sb.AppendLine('| Case | start/stop | selected encoder | retries | MP4 | duration | result |')
$null = $sb.AppendLine('|---|---|---|---|---|---|---|')
foreach ($r in $results) {
    # SKIPPED is deliberately distinct from FAILED: the case never ran (the worker did not
    # start), so a FAILED row would leave a wrong record about the product. Its MP4/duration
    # cells say n/a for the same reason -- nothing was produced to judge.
    $null = $sb.AppendLine(('| {0} | {1}/{2} | `{3}` | {4} | {5} | {6}s | {7} |' -f `
        $r.Case, $r.StartExit, $r.StopExit, $r.Selected, $r.FailedAttempts,
        $(if ($r.Skipped) { 'n/a' } elseif ($r.ValidMp4) { 'valid' } else { 'INVALID' }), $r.DurationSec,
        $(if ($r.Ok) { 'OK' } elseif ($r.Skipped) { "SKIPPED ($($r.Skipped))" } else { '**FAILED**' })))
}

$skippedCases = @($results | Where-Object { $_.Skipped })
if ($skippedCases.Count -gt 0) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('## Skipped cases (the app never started -- nothing was verified)')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('These rows say nothing about the product. The launcher''s own stderr is below;')
    $null = $sb.AppendLine('the work dir is kept so activity.log can be inspected.')
    foreach ($r in $skippedCases) {
        $null = $sb.AppendLine()
        $null = $sb.AppendLine("### $($r.Case)")
        $null = $sb.AppendLine('```')
        $null = $sb.AppendLine($(if ($r.StartStdErr) { $r.StartStdErr } else { '(the launcher printed nothing)' }))
        $null = $sb.AppendLine('```')
    }
}

$short = @($results | Where-Object { $_.Shortfall })
if ($short.Count -gt 0) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('## Short recordings')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('Recording begins at the first I-frame. If the ring buffer (BufferDuration) holds no')
    $null = $sb.AppendLine('keyframe, the entire pre-buffer is discarded AND live frames keep being discarded')
    $null = $sb.AppendLine('until the next one arrives -- so a long GOP silently destroys the pre-buffer contract.')
    $null = $sb.AppendLine('Check the selected encoder''s gop-size / key-int-max against BufferDuration first;')
    $null = $sb.AppendLine('frame dropping is a much rarer cause. On a GPU-less machine the D3D12 path also runs')
    $null = $sb.AppendLine('on the WARP software rasterizer, which adds its own shortfall.')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('This metric is inherently noisy -- the same configuration can differ by seconds')
    $null = $sb.AppendLine('depending on where the GOP boundary falls relative to start-recording. Do not build')
    $null = $sb.AppendLine('an automated gate on it.')
    $null = $sb.AppendLine()
    foreach ($r in $short) { $null = $sb.AppendLine("- $($r.Case): $($r.Shortfall)") }
}

$diagCases = @($results | Where-Object { $_.Diagnostics })
if ($diagCases.Count -gt 0) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('## Diagnostics for failed cases (captured with GST_DEBUG=4)')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('These cases initialised without error yet produced no valid MP4, i.e. they failed')
    $null = $sb.AppendLine('asynchronously -- neither ParseLaunch nor SetState(Playing) can detect that.')
    foreach ($r in $diagCases) {
        $null = $sb.AppendLine()
        $null = $sb.AppendLine("### $($r.Case)")
        $null = $sb.AppendLine('```')
        $null = $sb.AppendLine($(if ($r.Diagnostics) { $r.Diagnostics } else { '(no matching diagnostic lines captured)' }))
        $null = $sb.AppendLine('```')
    }
}
$null = $sb.AppendLine()
$null = $sb.AppendLine('## Encoder log lines per case')
foreach ($r in $results) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("### $($r.Case)")
    $null = $sb.AppendLine('```')
    $null = $sb.AppendLine($r.LogLines)
    $null = $sb.AppendLine('```')
}
Set-Content -Path $reportPath -Value $sb.ToString() -Encoding utf8

Write-Host '---------------------------------------------------------------'
# Result column instead of Ok: a SKIPPED case has Ok=False but is NOT a product failure,
# and this table is the last thing a human reads.
$results |
    Select-Object Case, StartExit, StopExit, Selected, DurationSec,
        @{ Name = 'Result'; Expression = { if ($_.Ok) { 'OK' } elseif ($_.Skipped) { 'SKIPPED' } else { 'FAILED' } } } |
    Format-Table -AutoSize

# Read back what the app actually loaded. Do this AFTER the cases have run (activity.log only
# has gst.runtime once the app has started at least once), and print it next to the table so a
# mismatch cannot be missed while reading the results.
Verify-BinMatchesTheAppsChoice -GstBin $gstBin
$null = Test-BundledRuntimeWasExercised -PublishDirectory $PublishDir

Write-Host "Report written to: $reportPath"

$skipped = @($results | Where-Object { $_.Skipped })
if ($skipped.Count -gt 0) {
    Write-Host ("SKIPPED cases (the worker did not start; nothing was verified): {0}" -f $skipped.Count) -ForegroundColor Yellow
}
$failed = @($results | Where-Object { -not $_.Ok -and -not $_.Skipped })
if ($failed.Count -gt 0) {
    Write-Host ("FAILED cases: {0}" -f $failed.Count) -ForegroundColor Red
}

# A skipped case verified NOTHING, so the run is not green either -- exiting 0 here would be
# the same class of false green as a filter that selects no tests. Keep the work dir too:
# the reason (activity.log, the launcher's stderr) only exists there.
$inconclusive = $failed.Count + $skipped.Count

if (-not $KeepWorkDir -and $inconclusive -eq 0) {
    Copy-Item $reportPath (Join-Path ([System.IO.Path]::GetTempPath()) 'gpu-encoder-report.md') -Force
    Remove-Item -Recurse -Force $WorkDir
    Write-Host "Work dir removed (report copied to %TEMP%\gpu-encoder-report.md). Use -KeepWorkDir to retain artefacts."
}

# Put the caller's PATH back (see the comment where it was prepended). Running this script
# twice in one console must not leave the first run's GStreamer ahead of the second's.
Restore-CallerPath

exit $(if ($inconclusive -gt 0) { 1 } else { 0 })
