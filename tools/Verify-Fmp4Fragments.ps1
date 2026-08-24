<#
.SYNOPSIS
    Measures whether the bundled GStreamer's mp4mux can produce fragmented MP4 that is
    usable for live streaming (fMP4 / CMAF-style delivery over a non-seekable sink).

.DESCRIPTION
    This answers three questions with measurements, not with documentation:

      1. Does every 'moof' start with a sync sample (IDR)?  A fragment whose first sample
         is not a sync sample cannot be an independently decodable media segment, so a
         client that joins there decodes garbage.
      2. Do 'ftyp' and a 'moov' containing 'mvex' precede the first 'moof'?  That pair is
         the initialization segment; without it (or with it written only at EOS) there is
         nothing to hand a player before the first media segment.
      3. Does the same pipeline run clean into a NON-SEEKABLE sink?  A muxer that seeks
         backwards to patch sizes works with filesink and breaks on a socket/appsink.

    Three runs are made, all with the same pipeline except for the sink:

      filesink  - seekable reference. stderr captured with progress messages enabled.
      fdsink    - non-seekable. Run with -q so the muxer's bytes are the ONLY thing on
                  stdout, which is captured verbatim and parsed like the reference.
      fakesink  - non-seekable. Run WITHOUT -q so bus WARNING/ERROR messages are visible;
                  this is the run judgment 3 reads.  (-q suppresses progress information
                  only, but the split keeps that fact from having to be trusted.)

    The MP4 is parsed here, box by box, rather than handed to gst-discoverer: the
    questions above are about tfhd/trun/tfdt bit fields that no discoverer reports.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the
    file has a BOM, and non-ASCII literals in a script that gets copied between machines
    are a reliable way to break it.

    This script does not touch product code or product settings.  It drives
    gst-launch-1.0 directly.

.PARAMETER GStreamerBin
    Directory holding gst-launch-1.0.exe / gst-inspect-1.0.exe.
    Defaults to the runtime bundled in this repository.

.PARAMETER WorkDir
    Where to put the produced MP4s, the captured stderr and the report.
    Defaults to a new folder under %TEMP%.

.PARAMETER NumBuffers
    Frames to encode. 300 at 30 fps is 10 seconds, i.e. about 10 fragments at 1000 ms.

.PARAMETER Framerate
    Source frame rate, frames per second.

.PARAMETER GopFrames
    Keyframe interval in frames. The default (60 at 30 fps = 2 s) is deliberately LONGER
    than the fragment duration: that is the interesting case, because it forces mp4mux to
    decide between cutting a fragment mid-GOP and stretching it to the next keyframe.

.PARAMETER FragmentMs
    mp4mux fragment-duration, in milliseconds.

.PARAMETER Encoder
    Force a specific H.264 encoder element instead of picking the first available one.

.PARAMETER KeepWorkDir
    Keep the work directory (MP4s, stderr, report) even when every judgment passes.

.PARAMETER Spike
    Run the one-off measurement spike instead of the three-run verification above.
    For every mp4mux fragment-mode value (discovered live via gst-inspect-1.0, never
    hard-coded) this writes to a seekable filesink and measures: the box list of a
    mid-write snapshot, the box list after a clean EOS, the box list after the writer
    process is killed mid-stream, whether a kill'd file still demuxes end to end
    (qtdemux ! h264parse ! fakesink) and its gst-discoverer-1.0 duration, plus one
    faststart=true (non-fragmented, current default) reference snapshot. Writes an
    MSE smoke-test page (check.html) alongside the MP4s. Always keeps WorkDir.

.PARAMETER SpikeSeconds
    Total source duration for each -Spike run, in seconds.

.PARAMETER SpikeSampleSeconds
    When to take the mid-write snapshot / kill the process, in seconds from start.
    Must be less than SpikeSeconds.

.EXAMPLE
    .\Verify-Fmp4Fragments.ps1
    .\Verify-Fmp4Fragments.ps1 -GopFrames 30 -KeepWorkDir
    .\Verify-Fmp4Fragments.ps1 -Spike

.OUTPUTS
    A markdown report at <WorkDir>\fmp4-fragment-report.md and the same summary on stdout.
    Exit code 0 only if all three judgments are conclusive PASS; 1 otherwise.
    With -Spike: measurement tables on stdout and artefacts (MP4s, check.html) under
    WorkDir (default %TEMP%\prapp-fmp4-spike); exit code is always 0.
#>
[CmdletBinding()]
param(
    [string]$GStreamerBin,
    [string]$WorkDir,
    [int]$NumBuffers = 300,
    [int]$Framerate  = 30,
    [int]$GopFrames  = 60,
    [int]$FragmentMs = 1000,
    [string]$Encoder = '',
    [switch]$KeepWorkDir,
    [switch]$Spike,
    [int]$SpikeSeconds = 10,
    [int]$SpikeSampleSeconds = 3
)

$ErrorActionPreference = 'Stop'

# A .ps1 runs INSIDE the calling PowerShell process, so every environment variable set
# here survives until that console is closed and is inherited by everything started from
# it afterwards. Capture and restore, exactly as Verify-GpuEncoders.ps1 does.
$script:CallerPath          = $env:PATH
$script:CallerPluginPath    = $env:GST_PLUGIN_PATH
$script:CallerPluginSysPath = $env:GST_PLUGIN_SYSTEM_PATH

function Restore-CallerEnvironment {
    if ($null -ne $script:CallerPath) { $env:PATH = $script:CallerPath }
    $env:GST_PLUGIN_PATH        = $script:CallerPluginPath
    $env:GST_PLUGIN_SYSTEM_PATH = $script:CallerPluginSysPath
}

# `break` rethrows, so this adds the cleanup without swallowing the failure.
trap {
    Restore-CallerEnvironment
    break
}

# ---------------------------------------------------------------- setup

if (-not $GStreamerBin) {
    $GStreamerBin = Join-Path $PSScriptRoot '..\src\GStreamer.GstSharpNet\runtimes\win-x64\bin'
}
$GStreamerBin = [System.IO.Path]::GetFullPath($GStreamerBin)

$gstLaunch  = Join-Path $GStreamerBin 'gst-launch-1.0.exe'
$gstInspect = Join-Path $GStreamerBin 'gst-inspect-1.0.exe'
if (-not (Test-Path $gstLaunch))  { throw "gst-launch-1.0.exe not found under '$GStreamerBin'. Pass -GStreamerBin." }
if (-not (Test-Path $gstInspect)) { throw "gst-inspect-1.0.exe not found under '$GStreamerBin'. Pass -GStreamerBin." }

if (-not $WorkDir) {
    if ($Spike) {
        $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) 'prapp-fmp4-spike'
    } else {
        $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pra-fmp4-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    }
}
$null = New-Item -ItemType Directory -Force $WorkDir
$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)

# The exe sits next to its DLLs, so the loader finds them either way; prepending is what
# makes a copied-elsewhere bin directory work too.
$env:PATH = "$GStreamerBin;$env:PATH"

# ---------------------------------------------------------------- process helpers

# Never use PowerShell's own '>' / '2>' redirection on a native exe here:
#   - 5.1 decodes a native command's stdout as TEXT and re-encodes it, which corrupts the
#     MP4 bytes that the fdsink run exists to capture;
#   - '2>&1' on a native exe wraps every stderr line in an ErrorRecord and makes $? false
#     even on exit code 0 (see .claude/rules/powershell.md).
# System.Diagnostics.Process gives raw bytes on the stdout BaseStream and keeps stderr a
# plain string.
#
# NOTE: never use Start-Process -Wait either -- it waits for the whole process tree.
function Invoke-GstLaunch {
    param(
        [string]$Arguments,
        [string]$StdOutFile = '',       # non-empty => copy stdout to this file as raw bytes
        [int]$TimeoutMs = 180000
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $gstLaunch
    $psi.Arguments              = $Arguments
    # Run in the work dir so 'filesink location=<bare name>' resolves there. That keeps
    # absolute paths -- and therefore quoting and backslash escaping -- out of the
    # pipeline string entirely: gst-launch joins argv with spaces before parsing, so a
    # path with a space in it would otherwise split the pipeline description.
    $psi.WorkingDirectory       = $WorkDir
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    $p = [System.Diagnostics.Process]::Start($psi)

    # Start draining BOTH pipes before waiting. A synchronous read defeats the timeout and
    # deadlocks once the other pipe's buffer fills.
    $fs       = $null
    $copyTask = $null
    $outTask  = $null
    if ($StdOutFile) {
        $fs       = [System.IO.File]::Create($StdOutFile)
        $copyTask = $p.StandardOutput.BaseStream.CopyToAsync($fs)
    } else {
        $outTask = $p.StandardOutput.ReadToEndAsync()
    }
    $errTask = $p.StandardError.ReadToEndAsync()

    $timedOut = -not $p.WaitForExit($TimeoutMs)
    if ($timedOut) {
        try { $p.Kill() } catch { }
        $null = $p.WaitForExit(5000)
    }

    $out = ''
    $err = ''
    try { if ($copyTask) { $null = $copyTask.Wait(30000) } } catch { }
    try { if ($outTask -and $outTask.Wait(10000)) { $out = $outTask.Result } } catch { }
    try { if ($errTask.Wait(10000)) { $err = $errTask.Result } } catch { }
    if ($fs) { $fs.Dispose() }

    $exit = -1
    if (-not $timedOut) { $exit = $p.ExitCode }

    return [pscustomobject]@{
        ExitCode = $exit
        TimedOut = $timedOut
        StdOut   = $out
        StdErr   = $err
    }
}

# gst-inspect is run through Process for the same reason: in 5.1 a native command writing
# to stderr becomes a NativeCommandError, which would abort the loop on the very elements
# that legitimately do not exist on this machine.
function Test-GstElement {
    param([string]$Name)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $gstInspect
    $psi.Arguments              = $Name
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $timedOut = -not $p.WaitForExit(60000)
    if ($timedOut) { try { $p.Kill() } catch { }; $null = $p.WaitForExit(5000) }
    try { $null = $outTask.Wait(5000) } catch { }
    try { $null = $errTask.Wait(5000) } catch { }
    if ($timedOut) { return $false }
    return ($p.ExitCode -eq 0)
}

# ---------------------------------------------------------------- ISO-BMFF reader

# Big-endian readers. Written as arithmetic rather than shifts so no intermediate is ever
# a signed 32-bit value: a size or a flags word with the top bit set would otherwise come
# back negative and every comparison against it would be wrong.
function Read-U32 {
    param([byte[]]$Data, [int]$Offset)
    return ([int64]$Data[$Offset] * 16777216) + ([int64]$Data[$Offset + 1] * 65536) +
           ([int64]$Data[$Offset + 2] * 256) + [int64]$Data[$Offset + 3]
}

function Read-U64 {
    param([byte[]]$Data, [int]$Offset)
    $v = [int64]0
    for ($i = 0; $i -lt 8; $i++) { $v = ($v * 256) + [int64]$Data[$Offset + $i] }
    return $v
}

function Read-BoxType {
    param([byte[]]$Data, [int]$Offset)
    return [System.Text.Encoding]::ASCII.GetString($Data, $Offset, 4)
}

# One level of boxes between [Start, End). Returns objects carrying the content range so
# callers can descend without recomputing header sizes.
function Get-Boxes {
    param([byte[]]$Data, [int]$Start, [int]$End)

    $boxes = New-Object System.Collections.Generic.List[object]
    $pos = $Start
    while (($pos + 8) -le $End) {
        $size = Read-U32 $Data $pos
        $type = Read-BoxType $Data ($pos + 4)
        $hdr  = 8
        if ($size -eq 1) {
            if (($pos + 16) -gt $End) { break }
            $size = Read-U64 $Data ($pos + 8)
            $hdr  = 16
        } elseif ($size -eq 0) {
            $size = $End - $pos          # 'to end of file'
        }
        if ($size -lt $hdr) { break }

        $truncated = (($pos + $size) -gt $End)
        $contentEnd = $End
        if (-not $truncated) { $contentEnd = [int]($pos + $size) }

        $boxes.Add([pscustomobject]@{
            Type         = $type
            Start        = $pos
            Size         = $size
            ContentStart = $pos + $hdr
            ContentEnd   = $contentEnd
            Truncated    = $truncated
        })
        if ($truncated) { break }
        $pos = [int]($pos + $size)
    }
    return $boxes
}

function Get-ChildBox {
    param([byte[]]$Data, $Parent, [string]$Type, [int]$SkipBytes = 0)
    if ($null -eq $Parent) { return $null }
    foreach ($b in (Get-Boxes -Data $Data -Start ($Parent.ContentStart + $SkipBytes) -End $Parent.ContentEnd)) {
        if ($b.Type -eq $Type) { return $b }
    }
    return $null
}

function Get-ChildBoxes {
    param([byte[]]$Data, $Parent, [string]$Type, [int]$SkipBytes = 0)
    $found = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Parent) { return $found }
    foreach ($b in (Get-Boxes -Data $Data -Start ($Parent.ContentStart + $SkipBytes) -End $Parent.ContentEnd)) {
        if ($b.Type -eq $Type) { $found.Add($b) }
    }
    return $found
}

# sample_flags is a packed 32-bit field (ISO/IEC 14496-12, 8.8.3.1):
#   bit(4) reserved | 2 is_leading | 2 sample_depends_on | 2 sample_is_depended_on
#   | 2 sample_has_redundancy | 3 sample_padding_value | 1 sample_is_non_sync_sample
#   | 16 sample_degradation_priority
# so sample_is_non_sync_sample is the single bit 0x00010000 and sample_depends_on is
# bits 25..24. sample_depends_on == 2 means "does not depend on others", i.e. an I-frame;
# it is reported next to the sync bit because a muxer that sets one and not the other is
# exactly the ambiguity this script exists to expose.
function Get-SampleFlagFacts {
    param([int64]$Flags)
    return [pscustomobject]@{
        Value      = $Flags
        IsSync     = (($Flags -band 0x00010000) -eq 0)
        DependsOn  = (($Flags -shr 24) -band 3)
        IsLeading  = (($Flags -shr 26) -band 3)
    }
}

# 'trex' carries the movie-wide default sample flags. It is the LAST fallback for the
# first sample's flags, and skipping it would turn "nothing said anywhere" into a false
# PASS on a file whose defaults actually declare non-sync samples.
function Get-TrexDefaultSampleFlags {
    param([byte[]]$Data, $Moov)
    $mvex = Get-ChildBox -Data $Data -Parent $Moov -Type 'mvex'
    if ($null -eq $mvex) { return $null }
    $trex = Get-ChildBox -Data $Data -Parent $mvex -Type 'trex'
    if ($null -eq $trex) { return $null }
    # version/flags(4) track_ID(4) default_sample_description_index(4)
    # default_sample_duration(4) default_sample_size(4) default_sample_flags(4)
    if (($trex.ContentStart + 24) -gt $trex.ContentEnd) { return $null }
    return (Read-U32 $Data ($trex.ContentStart + 20))
}

# The media timescale, so tfdt can be reported in milliseconds. Read from the first
# track's mdhd; mvhd's timescale is the movie timescale and is NOT what tfdt uses.
function Get-MediaTimescale {
    param([byte[]]$Data, $Moov)
    if ($null -eq $Moov) { return $null }
    foreach ($trak in (Get-ChildBoxes -Data $Data -Parent $Moov -Type 'trak')) {
        $mdia = Get-ChildBox -Data $Data -Parent $trak -Type 'mdia'
        $mdhd = Get-ChildBox -Data $Data -Parent $mdia -Type 'mdhd'
        if ($null -eq $mdhd) { continue }
        $ver = $Data[$mdhd.ContentStart]
        if ($ver -eq 1) { return (Read-U32 $Data ($mdhd.ContentStart + 4 + 16)) }
        return (Read-U32 $Data ($mdhd.ContentStart + 4 + 8))
    }
    return $null
}

# avc1 / avcC located structurally (moov/trak/mdia/minf/stbl/stsd/avc1/avcC) rather than
# by searching the moov bytes for the string 'avcC': a byte search also matches the same
# four characters appearing inside sample data or a free box, so it can say yes about a
# file that has no H.264 sample entry at all.
function Get-VideoSampleEntry {
    param([byte[]]$Data, $Moov)
    if ($null -eq $Moov) { return $null }
    foreach ($trak in (Get-ChildBoxes -Data $Data -Parent $Moov -Type 'trak')) {
        $mdia = Get-ChildBox -Data $Data -Parent $trak -Type 'mdia'
        $minf = Get-ChildBox -Data $Data -Parent $mdia -Type 'minf'
        $stbl = Get-ChildBox -Data $Data -Parent $minf -Type 'stbl'
        # stsd is a FullBox with an entry_count before the sample entries: 4 + 4 bytes.
        $stsd = Get-ChildBox -Data $Data -Parent $stbl -Type 'stsd'
        if ($null -eq $stsd) { continue }
        foreach ($entry in (Get-Boxes -Data $Data -Start ($stsd.ContentStart + 8) -End $stsd.ContentEnd)) {
            # VisualSampleEntry has 78 bytes of fixed fields before its child boxes.
            $avcC = $null
            if (($entry.ContentStart + 78) -lt $entry.ContentEnd) {
                foreach ($c in (Get-Boxes -Data $Data -Start ($entry.ContentStart + 78) -End $entry.ContentEnd)) {
                    if ($c.Type -eq 'avcC') { $avcC = $c }
                }
            }
            return [pscustomobject]@{ Format = $entry.Type; HasAvcC = ($null -ne $avcC) }
        }
    }
    return $null
}

# mvhd (movie header, direct child of moov) carries the movie-wide duration that a
# non-fragmented or in-place-patched muxer rewrites at EOS. Distinct from mdhd (per-track,
# read by Get-MediaTimescale above): mixing the two would silently compare the wrong field.
function Get-MvhdInfo {
    param([byte[]]$Data, $Moov)
    if ($null -eq $Moov) { return $null }
    $mvhd = Get-ChildBox -Data $Data -Parent $Moov -Type 'mvhd'
    if ($null -eq $mvhd) { return $null }
    $ver = $Data[$mvhd.ContentStart]
    if ($ver -eq 1) {
        $timescale = Read-U32 $Data ($mvhd.ContentStart + 4 + 16)
        $duration  = Read-U64 $Data ($mvhd.ContentStart + 4 + 20)
    } else {
        $timescale = Read-U32 $Data ($mvhd.ContentStart + 4 + 8)
        $duration  = Read-U32 $Data ($mvhd.ContentStart + 4 + 12)
    }
    return [pscustomobject]@{ Version = $ver; Timescale = $timescale; Duration = $duration }
}

# avc1.PPCCLL codec string (ISO/IEC 14496-15) read directly from avcC: byte 1 is
# AVCProfileIndication, byte 2 profile_compatibility, byte 3 AVCLevelIndication. Used to
# drive the MSE smoke-test page with the codec the spike actually produced, not a guess.
function Get-AvcCCodecString {
    param([byte[]]$Data, $Moov)
    if ($null -eq $Moov) { return $null }
    foreach ($trak in (Get-ChildBoxes -Data $Data -Parent $Moov -Type 'trak')) {
        $mdia = Get-ChildBox -Data $Data -Parent $trak -Type 'mdia'
        $minf = Get-ChildBox -Data $Data -Parent $mdia -Type 'minf'
        $stbl = Get-ChildBox -Data $Data -Parent $minf -Type 'stbl'
        $stsd = Get-ChildBox -Data $Data -Parent $stbl -Type 'stsd'
        if ($null -eq $stsd) { continue }
        foreach ($entry in (Get-Boxes -Data $Data -Start ($stsd.ContentStart + 8) -End $stsd.ContentEnd)) {
            if (($entry.ContentStart + 78) -ge $entry.ContentEnd) { continue }
            foreach ($c in (Get-Boxes -Data $Data -Start ($entry.ContentStart + 78) -End $entry.ContentEnd)) {
                if ($c.Type -ne 'avcC') { continue }
                if (($c.ContentStart + 4) -gt $c.ContentEnd) { continue }
                return ('avc1.{0:X2}{1:X2}{2:X2}' -f $Data[$c.ContentStart + 1], $Data[$c.ContentStart + 2], $Data[$c.ContentStart + 3])
            }
        }
    }
    return $null
}

function Get-TrafInfo {
    param([byte[]]$Data, $Traf, $TrexDefaultSampleFlags)

    $tfhd = Get-ChildBox -Data $Data -Parent $Traf -Type 'tfhd'
    $tfdt = Get-ChildBox -Data $Data -Parent $Traf -Type 'tfdt'
    $trun = Get-ChildBox -Data $Data -Parent $Traf -Type 'trun'

    $trackId            = $null
    $tfhdDefaultFlags   = $null
    if ($null -ne $tfhd) {
        $o     = $tfhd.ContentStart
        $flags = ([int64]$Data[$o + 1] * 65536) + ([int64]$Data[$o + 2] * 256) + [int64]$Data[$o + 3]
        $o    += 4
        $trackId = Read-U32 $Data $o; $o += 4
        if ($flags -band 0x000001) { $o += 8 }   # base-data-offset-present
        if ($flags -band 0x000002) { $o += 4 }   # sample-description-index-present
        if ($flags -band 0x000008) { $o += 4 }   # default-sample-duration-present
        if ($flags -band 0x000010) { $o += 4 }   # default-sample-size-present
        if ($flags -band 0x000020) { $tfhdDefaultFlags = Read-U32 $Data $o; $o += 4 }
    }

    $baseMediaDecodeTime = $null
    if ($null -ne $tfdt) {
        $ver = $Data[$tfdt.ContentStart]
        if ($ver -eq 1) { $baseMediaDecodeTime = Read-U64 $Data ($tfdt.ContentStart + 4) }
        else            { $baseMediaDecodeTime = Read-U32 $Data ($tfdt.ContentStart + 4) }
    }

    $sampleCount      = $null
    $firstSampleFlags = $null
    $sample0Flags     = $null
    $trunFlags        = $null
    if ($null -ne $trun) {
        $o          = $trun.ContentStart
        $trunFlags  = ([int64]$Data[$o + 1] * 65536) + ([int64]$Data[$o + 2] * 256) + [int64]$Data[$o + 3]
        $o         += 4
        $sampleCount = Read-U32 $Data $o; $o += 4
        if ($trunFlags -band 0x000001) { $o += 4 }                                   # data-offset-present
        if ($trunFlags -band 0x000004) { $firstSampleFlags = Read-U32 $Data $o; $o += 4 }
        if ($trunFlags -band 0x000400) {
            $so = $o
            if ($trunFlags -band 0x000100) { $so += 4 }   # sample-duration-present
            if ($trunFlags -band 0x000200) { $so += 4 }   # sample-size-present
            if (($so + 4) -le $trun.ContentEnd) { $sample0Flags = Read-U32 $Data $so }
        }
    }

    # Precedence for the FIRST sample of the fragment, most specific first.
    $source = 'none (absent everywhere; defaults to 0 => sync)'
    $eff    = [int64]0
    if ($null -ne $firstSampleFlags)      { $eff = $firstSampleFlags;      $source = 'trun.first_sample_flags' }
    elseif ($null -ne $sample0Flags)      { $eff = $sample0Flags;          $source = 'trun.sample_flags[0]' }
    elseif ($null -ne $tfhdDefaultFlags)  { $eff = $tfhdDefaultFlags;      $source = 'tfhd.default_sample_flags' }
    elseif ($null -ne $TrexDefaultSampleFlags) { $eff = $TrexDefaultSampleFlags; $source = 'trex.default_sample_flags' }

    $facts = Get-SampleFlagFacts -Flags $eff

    return [pscustomobject]@{
        TrackId             = $trackId
        BaseMediaDecodeTime = $baseMediaDecodeTime
        SampleCount         = $sampleCount
        TfhdDefaultFlags    = $tfhdDefaultFlags
        TrunFlags           = $trunFlags
        FirstSampleFlags    = $eff
        FlagsSource         = $source
        FirstIsSync         = $facts.IsSync
        DependsOn           = $facts.DependsOn
        HasTfdt             = ($null -ne $tfdt)
        HasTrun             = ($null -ne $trun)
    }
}

function Get-Fmp4Analysis {
    param([string]$Path)

    $data = [System.IO.File]::ReadAllBytes($Path)
    $top  = Get-Boxes -Data $data -Start 0 -End $data.Length

    $moov = $null
    foreach ($b in $top) { if ($b.Type -eq 'moov') { $moov = $b; break } }

    $hasMvex = $false
    if ($null -ne $moov) { $hasMvex = ($null -ne (Get-ChildBox -Data $data -Parent $moov -Type 'mvex')) }

    $timescale = Get-MediaTimescale -Data $data -Moov $moov
    $trexFlags = Get-TrexDefaultSampleFlags -Data $data -Moov $moov
    $sampleEnt = Get-VideoSampleEntry -Data $data -Moov $moov
    $mvhd      = Get-MvhdInfo -Data $data -Moov $moov
    $avcCodec  = Get-AvcCCodecString -Data $data -Moov $moov

    $fragments = New-Object System.Collections.Generic.List[object]
    $index = 0
    $prevMs = $null
    foreach ($b in $top) {
        if ($b.Type -ne 'moof') { continue }
        $traf = Get-ChildBox -Data $data -Parent $b -Type 'traf'
        $info = Get-TrafInfo -Data $data -Traf $traf -TrexDefaultSampleFlags $trexFlags

        $ms = $null
        $deltaMs = $null
        if (($null -ne $info.BaseMediaDecodeTime) -and $timescale -and ($timescale -gt 0)) {
            $ms = [math]::Round(($info.BaseMediaDecodeTime * 1000.0) / $timescale, 1)
            if ($null -ne $prevMs) { $deltaMs = [math]::Round($ms - $prevMs, 1) }
            $prevMs = $ms
        }

        $fragments.Add([pscustomobject]@{
            Index               = $index
            Offset              = $b.Start
            TrackId             = $info.TrackId
            BaseMediaDecodeTime = $info.BaseMediaDecodeTime
            TfdtMs              = $ms
            DeltaMs             = $deltaMs
            SampleCount         = $info.SampleCount
            FirstSampleFlags    = ('0x{0:X8}' -f [uint32]$info.FirstSampleFlags)
            FlagsSource         = $info.FlagsSource
            FirstIsSync         = $info.FirstIsSync
            DependsOn           = $info.DependsOn
        })
        $index++
    }

    $order = @()
    foreach ($b in $top) { $order += $b.Type }

    # Hash of the concatenated mdat payloads. This is what turns "the two runs look alike"
    # into "the two runs carry the SAME media": without it, a sink that silently dropped or
    # reordered encoded frames could still produce a matching box order and a matching
    # fragment table.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($b in $top) {
            if ($b.Type -ne 'mdat') { continue }
            $len = [int]($b.ContentEnd - $b.ContentStart)
            if ($len -gt 0) { $null = $sha.TransformBlock($data, [int]$b.ContentStart, $len, $null, 0) }
        }
        $null = $sha.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        $mdatHash = [System.BitConverter]::ToString($sha.Hash).Replace('-', '')
    } finally { $sha.Dispose() }

    # Everything up to and including the last mdat is what a live consumer ever sees; what
    # follows is written at EOS only.
    $lastMdat = -1
    for ($i = 0; $i -lt $order.Count; $i++) { if ($order[$i] -eq 'mdat') { $lastMdat = $i } }
    $streamingOrder = @()
    $trailingOrder  = @()
    if ($lastMdat -ge 0) {
        $streamingOrder = @($order[0..$lastMdat])
        if (($lastMdat + 1) -lt $order.Count) { $trailingOrder = @($order[($lastMdat + 1)..($order.Count - 1)]) }
    } else {
        $streamingOrder = @($order)
    }

    $firstMoofIndex = -1
    for ($i = 0; $i -lt $order.Count; $i++) { if ($order[$i] -eq 'moof') { $firstMoofIndex = $i; break } }
    $ftypIndex = [array]::IndexOf($order, 'ftyp')
    $moovIndex = [array]::IndexOf($order, 'moov')

    $moovBox = $null
    if ($null -ne $moov) { $moovBox = $top | Where-Object { $_.Type -eq 'moov' } | Select-Object -First 1 }

    return [pscustomobject]@{
        Path            = $Path
        Bytes           = $data.Length
        TopLevelOrder   = $order
        StreamingOrder  = $streamingOrder
        TrailingOrder   = $trailingOrder
        MdatSha256      = $mdatHash
        HasMvex         = $hasMvex
        Timescale       = $timescale
        TrexFlags       = $trexFlags
        SampleFormat    = $(if ($sampleEnt) { $sampleEnt.Format } else { $null })
        HasAvcC         = $(if ($sampleEnt) { $sampleEnt.HasAvcC } else { $false })
        AvcCodec        = $avcCodec
        Fragments       = $fragments
        FtypIndex       = $ftypIndex
        MoovIndex       = $moovIndex
        FirstMoofIndex  = $firstMoofIndex
        Truncated       = ($null -ne ($top | Where-Object { $_.Truncated }))
        MoovStart       = $(if ($moovBox) { $moovBox.Start } else { $null })
        MoovSize        = $(if ($moovBox) { $moovBox.Size } else { $null })
        MvhdDuration    = $(if ($mvhd) { $mvhd.Duration } else { $null })
        MvhdTimescale   = $(if ($mvhd) { $mvhd.Timescale } else { $null })
        MvhdSeconds     = $(if (($mvhd) -and ($mvhd.Timescale -gt 0)) { [math]::Round($mvhd.Duration / $mvhd.Timescale, 2) } else { $null })
    }
}

# A run counts as clean only if the process succeeded AND the bus said nothing. Matching
# on stderr alone would call a crashed run clean whenever it happened to print nothing.
function Get-MessageLines {
    param([string]$Text)
    $lines = @()
    foreach ($l in ($Text -split "`r?`n")) {
        if ($l -match 'WARNING|ERROR|CRITICAL') { $lines += $l.Trim() }
    }
    return $lines
}

# ---------------------------------------------------------------- spike-mode helpers

# Copies a file that a live gst-launch process still has open for writing. filesink
# opens with FILE_SHARE_READ (not WRITE), so a plain Copy-Item intermittently hits a
# sharing violation right when a fragment is being flushed; retry briefly instead of
# treating that race as a failure.
function Copy-SnapshotFile {
    param([string]$Source, [string]$Dest)
    $attempt = 0
    while ($true) {
        try {
            $src = [System.IO.File]::Open($Source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
            try {
                $dst = [System.IO.File]::Create($Dest)
                try { $src.CopyTo($dst) } finally { $dst.Dispose() }
            } finally { $src.Dispose() }
            return
        } catch {
            $attempt++
            if ($attempt -ge 5) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

# Same Process-based launch as Invoke-GstLaunch, but returned as a live handle: the spike
# needs to sleep, snapshot the still-growing file, and only THEN decide whether to wait
# for EOS or kill the process -- Invoke-GstLaunch's single blocking Wait does not allow
# that interjection.
function Start-SpikeProcess {
    param([string]$Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $gstLaunch
    $psi.Arguments              = $Arguments
    $psi.WorkingDirectory       = $WorkDir
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    return [pscustomobject]@{
        Process = $p
        OutTask = $p.StandardOutput.ReadToEndAsync()
        ErrTask = $p.StandardError.ReadToEndAsync()
    }
}

function Wait-SpikeProcess {
    param($Handle, [int]$TimeoutMs = 60000)
    $timedOut = -not $Handle.Process.WaitForExit($TimeoutMs)
    if ($timedOut) { try { $Handle.Process.Kill() } catch { }; $null = $Handle.Process.WaitForExit(5000) }
    $out = ''; $err = ''
    try { if ($Handle.OutTask.Wait(10000)) { $out = $Handle.OutTask.Result } } catch { }
    try { if ($Handle.ErrTask.Wait(10000)) { $err = $Handle.ErrTask.Result } } catch { }
    $exit = -1
    if (-not $timedOut) { $exit = $Handle.Process.ExitCode }
    return [pscustomobject]@{ ExitCode = $exit; TimedOut = $timedOut; StdOut = $out; StdErr = $err }
}

# Simulates a crash: the process is killed outright (not asked to shut down), so only
# bytes already handed to the OS via write() are on disk -- exactly the scenario (c) asks
# about.
function Stop-SpikeProcess {
    param($Handle)
    try { $Handle.Process.Kill() } catch { }
    $null = $Handle.Process.WaitForExit(5000)
    $out = ''; $err = ''
    try { if ($Handle.OutTask.Wait(5000)) { $out = $Handle.OutTask.Result } } catch { }
    try { if ($Handle.ErrTask.Wait(5000)) { $err = $Handle.ErrTask.Result } } catch { }
    return [pscustomobject]@{ StdOut = $out; StdErr = $err }
}

# Does the file demux end to end? Frame count comes from 'gst-launch -v ... fakesink
# silent=false': that combination makes fakesink emit a deep-notify on its
# 'last-message' property for every buffer, and '-v' is what makes gst-launch print
# deep-notify at all. This build has debug logging compiled out (GST_DEBUG has no
# effect), so this notify path is the only per-buffer signal available from the CLI --
# confirmed empirically before writing this function, not assumed.
function Test-DemuxToEnd {
    param([string]$FileName)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $gstLaunch
    $psi.Arguments              = "-v filesrc location=$FileName ! qtdemux ! h264parse ! fakesink silent=false"
    $psi.WorkingDirectory       = $WorkDir
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $timedOut = -not $p.WaitForExit(60000)
    if ($timedOut) { try { $p.Kill() } catch { }; $null = $p.WaitForExit(5000) }
    $out = ''; $err = ''
    try { if ($outTask.Wait(10000)) { $out = $outTask.Result } } catch { }
    try { if ($errTask.Wait(10000)) { $err = $errTask.Result } } catch { }
    $exit = -1
    if (-not $timedOut) { $exit = $p.ExitCode }
    return [pscustomobject]@{
        ExitCode   = $exit
        TimedOut   = $timedOut
        FrameCount = ([regex]::Matches($out, 'last-message = chain')).Count
        GotEos     = ($out -match 'Got EOS')
        Messages   = @(Get-MessageLines ($out + "`n" + $err))
    }
}

# gst-discoverer-1.0.exe is NOT in the bundled runtime (verified: only gst-launch-1.0.exe
# and gst-inspect-1.0.exe ship there). Borrow the one from the full dev-machine install --
# safe only because both report the same 'GStreamer 1.28.6', checked by the caller before
# ever invoking this.
function Resolve-Discoverer {
    param([string]$Bin)
    $candidate = Join-Path $Bin 'gst-discoverer-1.0.exe'
    if (Test-Path $candidate) { return $candidate }
    if ($env:LOCALAPPDATA) {
        $fallback = Join-Path $env:LOCALAPPDATA 'Programs\gstreamer\1.0\msvc_x86_64\bin\gst-discoverer-1.0.exe'
        if (Test-Path $fallback) { return $fallback }
    }
    return $null
}

function Get-DiscovererDuration {
    param([string]$DiscovererExe, [string]$FileName)
    if (-not $DiscovererExe) { return 'gst-discoverer-1.0.exe not found' }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $DiscovererExe
    $psi.Arguments              = $FileName
    $psi.WorkingDirectory       = $WorkDir
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $null = $p.WaitForExit(30000)
    $out = ''
    try { if ($outTask.Wait(5000)) { $out = $outTask.Result } } catch { }
    try { $null = $errTask.Wait(5000) } catch { }
    $m = [regex]::Match($out, 'Duration:\s*([0-9:\.]+)')
    if ($m.Success) { return $m.Groups[1].Value }
    $firstLine = ($out -split "`r?`n" | Select-Object -First 1)
    return "unavailable ($firstLine)"
}

# Reads the fragment-mode enum and the streamable/faststart property descriptions
# straight from gst-inspect-1.0 output -- never hard-coded, so a runtime with a different
# GstQTMuxFragmentMode (e.g. a future GStreamer version adding a third mode) is measured
# as it actually is, not as remembered.
function Get-Mp4MuxFragmentModes {
    param([string]$GstInspectExe)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $GstInspectExe
    $psi.Arguments              = 'mp4mux'
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $null = $p.WaitForExit(30000)
    $out = ''
    try { if ($outTask.Wait(5000)) { $out = $outTask.Result } } catch { }
    try { $null = $errTask.Wait(5000) } catch { }
    # gst-inspect lists several OTHER enum properties too (e.g. qtmux's own sort-mode,
    # aggregator's start-time-selection) whose '(N): name - desc' lines look identical to
    # fragment-mode's. Scope the scan to the lines between the 'fragment-mode' property
    # header and the next blank line, or every enum in the whole element would be
    # mistaken for a fragment-mode value.
    $lines = $out -split "`r?`n"
    $startIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*fragment-mode\s') { $startIdx = $i; break } }
    $modes = New-Object System.Collections.Generic.List[object]
    if ($startIdx -ge 0) {
        for ($i = $startIdx + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq '') { break }
            $mm = [regex]::Match($lines[$i], '^\s*\((\d+)\):\s+(\S+)\s+-\s+(.+)$')
            if ($mm.Success) {
                $modes.Add([pscustomobject]@{ Value = [int]$mm.Groups[1].Value; Name = $mm.Groups[2].Value; Description = $mm.Groups[3].Value.Trim() })
            }
        }
    }
    return [pscustomobject]@{
        Modes          = $modes
        StreamableLine = (($lines | Where-Object { $_ -match '^\s*streamable\s' } | Select-Object -First 1))
        FaststartLine  = (($lines | Where-Object { $_ -match '^\s*faststart\s' } | Select-Object -First 1))
    }
}

function Format-BoxOrder {
    param($Order)
    if (-not $Order -or $Order.Count -eq 0) { return '(empty)' }
    if ($Order.Count -le 10) { return ($Order -join ' ') }
    $head = ($Order[0..4] -join ' ')
    $tail = ($Order[($Order.Count - 3)..($Order.Count - 1)] -join ' ')
    return "$head ... $tail (x$($Order.Count))"
}

function Invoke-Fmp4Spike {
    Write-Host "== fmp4 spike: fragment-mode enumeration (gst-inspect-1.0 mp4mux)"
    $modeInfo = Get-Mp4MuxFragmentModes -GstInspectExe $gstInspect
    foreach ($m in $modeInfo.Modes) { Write-Host ("  ({0}) {1} - {2}" -f $m.Value, $m.Name, $m.Description) }
    Write-Host "  streamable: $($modeInfo.StreamableLine)"
    Write-Host "  faststart : $($modeInfo.FaststartLine)"
    Write-Host ''

    $discoverer = Resolve-Discoverer -Bin $GStreamerBin
    Write-Host "gst-discoverer-1.0: $(if ($discoverer) { $discoverer } else { 'not found (duration will read as unavailable)' })"
    Write-Host ''

    # $encoderSpec (picked earlier for the base 3-run verification) does not set
    # low-latency behaviour: mfh264enc without 'low-latency=true' buffers several
    # seconds of lookahead before it emits a single frame, which made the very first
    # spike run (SpikeSeconds=3 SpikeSampleSeconds=1) observe 0 bytes at the sample
    # point on BOTH fragment-modes -- the encoder, not the muxer, was the bottleneck.
    # Confirmed by re-running with this override before trusting the mid-stream numbers.
    $spikeEncoderSpec = $encoderSpec
    if ($chosen.Name -eq 'mfh264enc') { $spikeEncoderSpec = "mfh264enc bitrate=2000 low-latency=true gop-size=$GopFrames" }
    if ($spikeEncoderSpec -ne $encoderSpec) { Write-Host "Spike encoder (low-latency override): $spikeEncoderSpec" }
    Write-Host ''

    $numBuffers = $SpikeSeconds * $Framerate
    $results = New-Object System.Collections.Generic.List[object]

    foreach ($mode in $modeInfo.Modes) {
        $tag          = "mode$($mode.Value)"
        $completeName = "$tag-complete.mp4"
        $snapshotName = "$tag-3s.mp4"
        $killName     = "$tag-kill.mp4"
        $head = "videotestsrc is-live=true num-buffers=$numBuffers ! video/x-raw,format=NV12,width=320,height=240,framerate=$Framerate/1 ! $spikeEncoderSpec ! h264parse ! mp4mux fragment-duration=$FragmentMs fragment-mode=$($mode.Value)"

        Write-Host "== $tag ($($mode.Name)): run to completion, snapshot at ${SpikeSampleSeconds}s"
        $hA = Start-SpikeProcess -Arguments "$head ! filesink location=$completeName"
        Start-Sleep -Seconds $SpikeSampleSeconds
        Copy-SnapshotFile -Source (Join-Path $WorkDir $completeName) -Dest (Join-Path $WorkDir $snapshotName)
        $runA = Wait-SpikeProcess -Handle $hA -TimeoutMs 60000
        Write-Host ("   complete run exit={0} timedOut={1}" -f $runA.ExitCode, $runA.TimedOut)

        Write-Host "== $tag ($($mode.Name)): separate run, kill at ${SpikeSampleSeconds}s"
        $hB = Start-SpikeProcess -Arguments "$head ! filesink location=$killName"
        Start-Sleep -Seconds $SpikeSampleSeconds
        $null = Stop-SpikeProcess -Handle $hB

        $aSnap = $null; $aComplete = $null; $aKill = $null
        if (Test-Path (Join-Path $WorkDir $snapshotName)) { $aSnap     = Get-Fmp4Analysis -Path (Join-Path $WorkDir $snapshotName) }
        if (Test-Path (Join-Path $WorkDir $completeName)) { $aComplete = Get-Fmp4Analysis -Path (Join-Path $WorkDir $completeName) }
        if (Test-Path (Join-Path $WorkDir $killName))     { $aKill     = Get-Fmp4Analysis -Path (Join-Path $WorkDir $killName) }

        $moofOffsetsStable = $null
        if (($null -ne $aSnap) -and ($null -ne $aComplete)) {
            $n = [Math]::Min($aSnap.Fragments.Count, $aComplete.Fragments.Count)
            if ($n -gt 0) {
                $stable = $true
                for ($i = 0; $i -lt $n; $i++) {
                    if ($aSnap.Fragments[$i].Offset -ne $aComplete.Fragments[$i].Offset) { $stable = $false; break }
                }
                $moofOffsetsStable = $stable
            }
        }

        $demuxComplete = $null
        if ($null -ne $aComplete) { $demuxComplete = Test-DemuxToEnd -FileName $completeName }
        $demuxKill = $null
        if ($null -ne $aKill) { $demuxKill = Test-DemuxToEnd -FileName $killName }

        $durComplete = Get-DiscovererDuration -DiscovererExe $discoverer -FileName $completeName
        $durKill     = Get-DiscovererDuration -DiscovererExe $discoverer -FileName $killName

        $results.Add([pscustomobject]@{
            Mode               = $mode.Name
            ModeValue          = $mode.Value
            SnapBoxes          = $(if ($aSnap) { Format-BoxOrder $aSnap.TopLevelOrder } else { '(no file)' })
            SnapHasMvex        = $(if ($aSnap) { $aSnap.HasMvex } else { $null })
            SnapMvhdSeconds    = $(if ($aSnap) { $aSnap.MvhdSeconds } else { $null })
            SnapBytes          = $(if ($aSnap) { $aSnap.Bytes } else { 0 })
            CompleteBoxes      = $(if ($aComplete) { Format-BoxOrder $aComplete.TopLevelOrder } else { '(no file)' })
            CompleteTrailing   = $(if ($aComplete) { $(if ($aComplete.TrailingOrder.Count) { $aComplete.TrailingOrder -join ' ' } else { '(none)' }) } else { $null })
            CompleteMoovStart  = $(if ($aComplete) { $aComplete.MoovStart } else { $null })
            CompleteMoovSize   = $(if ($aComplete) { $aComplete.MoovSize } else { $null })
            CompleteMvhdSeconds= $(if ($aComplete) { $aComplete.MvhdSeconds } else { $null })
            CompleteBytes      = $(if ($aComplete) { $aComplete.Bytes } else { 0 })
            MoofOffsetsStable  = $moofOffsetsStable
            CompleteDemuxExit  = $(if ($demuxComplete) { $demuxComplete.ExitCode } else { $null })
            CompleteDemuxGotEos= $(if ($demuxComplete) { $demuxComplete.GotEos } else { $null })
            CompleteDemuxFrames= $(if ($demuxComplete) { $demuxComplete.FrameCount } else { $null })
            KillBoxes          = $(if ($aKill) { Format-BoxOrder $aKill.TopLevelOrder } else { '(no file)' })
            KillBytes          = $(if ($aKill) { $aKill.Bytes } else { 0 })
            KillDemuxExit      = $(if ($demuxKill) { $demuxKill.ExitCode } else { $null })
            KillDemuxGotEos    = $(if ($demuxKill) { $demuxKill.GotEos } else { $null })
            KillDemuxFrames    = $(if ($demuxKill) { $demuxKill.FrameCount } else { $null })
            KillDemuxMessages  = $(if ($demuxKill) { ($demuxKill.Messages -join ' | ') } else { '' })
            DurationComplete   = $durComplete
            DurationKill       = $durKill
            CompleteFile       = (Join-Path $WorkDir $completeName)
            SnapshotFile       = (Join-Path $WorkDir $snapshotName)
            KillFile           = (Join-Path $WorkDir $killName)
        })
    }

    # (e) reference: current (non-fragmented) faststart=true behaviour, 3s snapshot only.
    Write-Host "== reference: faststart=true (fragment-duration=0, current default), snapshot at ${SpikeSampleSeconds}s"
    $fsName = 'faststart-3s.mp4'
    $fsSnapName = 'faststart-3s-snapshot.mp4'
    $headFs = "videotestsrc is-live=true num-buffers=$numBuffers ! video/x-raw,format=NV12,width=320,height=240,framerate=$Framerate/1 ! $spikeEncoderSpec ! h264parse ! mp4mux faststart=true"
    $hF = Start-SpikeProcess -Arguments "$headFs ! filesink location=$fsName"
    Start-Sleep -Seconds $SpikeSampleSeconds
    Copy-SnapshotFile -Source (Join-Path $WorkDir $fsName) -Dest (Join-Path $WorkDir $fsSnapName)
    $null = Stop-SpikeProcess -Handle $hF
    $aFs = $null
    if (Test-Path (Join-Path $WorkDir $fsSnapName)) { $aFs = Get-Fmp4Analysis -Path (Join-Path $WorkDir $fsSnapName) }

    Write-Host ''
    Write-Host '---------------------------------------------------------------'
    Write-Host 'fragment-mode spike results'
    Write-Host '---------------------------------------------------------------'
    foreach ($r in $results) {
        Write-Host ("mode {0} ({1})" -f $r.ModeValue, $r.Mode)
        Write-Host ("  (a) snapshot @${SpikeSampleSeconds}s : boxes=[$($r.SnapBoxes)] hasMvex=$($r.SnapHasMvex) mvhdSeconds=$($r.SnapMvhdSeconds) bytes=$($r.SnapBytes)")
        Write-Host ("  (b) EOS complete       : boxes=[$($r.CompleteBoxes)] trailing=[$($r.CompleteTrailing)] moovStart=$($r.CompleteMoovStart) moovSize=$($r.CompleteMoovSize) mvhdSeconds=$($r.CompleteMvhdSeconds) bytes=$($r.CompleteBytes) moofOffsetsStableVsSnapshot=$($r.MoofOffsetsStable)")
        Write-Host ("  (c) killed @${SpikeSampleSeconds}s    : boxes=[$($r.KillBoxes)] bytes=$($r.KillBytes) demux(exit=$($r.KillDemuxExit) gotEos=$($r.KillDemuxGotEos) frames=$($r.KillDemuxFrames) msgs=$($r.KillDemuxMessages))")
        Write-Host ("  (d) qtdemux end-to-end : complete(exit=$($r.CompleteDemuxExit) gotEos=$($r.CompleteDemuxGotEos) frames=$($r.CompleteDemuxFrames))  kill(exit=$($r.KillDemuxExit) gotEos=$($r.KillDemuxGotEos) frames=$($r.KillDemuxFrames))")
        Write-Host ("  (d) discoverer duration: complete=$($r.DurationComplete) kill=$($r.DurationKill)")
        Write-Host ''
    }
    if ($null -ne $aFs) {
        Write-Host ("(e) faststart=true @${SpikeSampleSeconds}s: boxes=[$(Format-BoxOrder $aFs.TopLevelOrder)] hasMoov=$($aFs.MoovIndex -ge 0) bytes=$($aFs.Bytes)")
    } else {
        Write-Host "(e) faststart=true @${SpikeSampleSeconds}s: 0 bytes written to the target sink yet"
    }
    Write-Host '---------------------------------------------------------------'

    # ---- MSE smoke-test page. fetch()/XHR do not work on file:// in Chromium/Edge, so
    # the page shows that failure and then does the real test via a <input type=file> +
    # Blob.slice appendBuffer walk at arbitrary (non-fragment-aligned) 4 KB boundaries.
    $firstOk = $results | Where-Object { $_.CompleteBytes -gt 0 } | Select-Object -First 1
    $codecStr = 'avc1.640014'
    if ($firstOk -and (Test-Path $firstOk.CompleteFile)) {
        $a = Get-Fmp4Analysis -Path $firstOk.CompleteFile
        if ($a.AvcCodec) { $codecStr = $a.AvcCodec }
    }
    $videoLis = ($results | ForEach-Object {
        $cn = "mode$($_.ModeValue)-complete.mp4"; $kn = "mode$($_.ModeValue)-kill.mp4"
        "<li>mode $($_.ModeValue) ($($_.Mode)) complete: <a href=`"$cn`">$cn</a> / kill: <a href=`"$kn`">$kn</a></li>"
    }) -join "`n    "
    $html = @"
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>fmp4 spike check</title></head>
<body>
<h1>fmp4 spike check</h1>
<h2>Files produced (open this page via file:// next to them)</h2>
<ul>
    $videoLis
</ul>
<h2>Plain video element playback / seek check</h2>
<p>Point each below at one file at a time by editing the src, or open the mp4 directly.</p>
<video id="v" controls style="width:480px" src="mode0-complete.mp4"></video>
<h2>MSE arbitrary-4KB-boundary appendBuffer test</h2>
<p>fetch() probe (expected to fail on file://):</p>
<pre id="fetchResult">(not run yet)</pre>
<p>Pick a local mp4 file to feed through MediaSource in 4KB chunks at arbitrary byte offsets (not aligned to fragment boundaries):</p>
<input type="file" id="picker" accept="video/mp4">
<video id="mse" controls style="width:480px"></video>
<pre id="mseLog"></pre>
<script>
var codec = 'video/mp4; codecs="$codecStr"';
fetch('mode0-complete.mp4').then(function(r){
  document.getElementById('fetchResult').textContent = 'fetch ok, status=' + r.status;
}).catch(function(e){
  document.getElementById('fetchResult').textContent = 'fetch failed as expected on file:// -> ' + e;
});

document.getElementById('picker').addEventListener('change', function(ev){
  var log = document.getElementById('mseLog');
  log.textContent = '';
  function line(s){ log.textContent += s + "\n"; }
  var file = ev.target.files[0];
  if (!file) { return; }
  if (!window.MediaSource || !MediaSource.isTypeSupported(codec)) {
    line('MediaSource.isTypeSupported(' + codec + ') = false in this browser'); return;
  }
  line('codec = ' + codec);
  var video = document.getElementById('mse');
  var ms = new MediaSource();
  video.src = URL.createObjectURL(ms);
  ms.addEventListener('sourceopen', function(){
    var sb;
    try { sb = ms.addSourceBuffer(codec); } catch (e) { line('addSourceBuffer threw: ' + e); return; }
    var CHUNK = 4096;
    var offset = 0;
    function pump(){
      if (offset >= file.size) { try { ms.endOfStream(); } catch (e) {} line('done, appended ' + offset + ' bytes total'); return; }
      var end = Math.min(offset + CHUNK, file.size);
      var slice = file.slice(offset, end);
      var reader = new FileReader();
      reader.onload = function(){
        try {
          sb.appendBuffer(new Uint8Array(reader.result));
        } catch (e) {
          line('appendBuffer threw at offset ' + offset + ': ' + e); return;
        }
        offset = end;
      };
      reader.readAsArrayBuffer(slice);
    }
    sb.addEventListener('updateend', pump);
    sb.addEventListener('error', function(){ line('sourceBuffer error at offset ' + offset); });
    pump();
  });
});
</script>
</body></html>
"@
    $checkHtmlPath = Join-Path $WorkDir 'check.html'
    Set-Content -Path $checkHtmlPath -Value $html -Encoding utf8
    Write-Host ''
    Write-Host "check.html written to: $checkHtmlPath"
    Write-Host "WorkDir (all artefacts): $WorkDir"
}

# ---------------------------------------------------------------- probe the runtime

Write-Host "GStreamer bin : $GStreamerBin"
Write-Host "Work dir      : $WorkDir"

$version = ''
$psiV = New-Object System.Diagnostics.ProcessStartInfo
$psiV.FileName = $gstInspect
$psiV.Arguments = '--version'
$psiV.UseShellExecute = $false
$psiV.RedirectStandardOutput = $true
$psiV.RedirectStandardError  = $true
$pV = [System.Diagnostics.Process]::Start($psiV)
$vOut = $pV.StandardOutput.ReadToEndAsync()
$vErr = $pV.StandardError.ReadToEndAsync()
$null = $pV.WaitForExit(60000)
try { if ($vOut.Wait(5000)) { $version = $vOut.Result } } catch { }
try { $null = $vErr.Wait(5000) } catch { }
$versionLine = (($version -split "`r?`n") | Where-Object { $_ -match 'GStreamer' } | Select-Object -First 1)
Write-Host "GStreamer     : $versionLine"

# The bundled layout puts plugins in ..\lib\gstreamer-1.0 relative to bin, and GStreamer
# normally derives that itself. Only set the variables if it does NOT -- setting them
# unconditionally would hide a broken layout and would also change which plugins a
# system-wide install loads.
if (-not (Test-GstElement 'mp4mux')) {
    $pluginDir = [System.IO.Path]::GetFullPath((Join-Path $GStreamerBin '..\lib\gstreamer-1.0'))
    Write-Host "mp4mux not visible; setting GST_PLUGIN_PATH / GST_PLUGIN_SYSTEM_PATH to $pluginDir"
    $env:GST_PLUGIN_PATH        = $pluginDir
    $env:GST_PLUGIN_SYSTEM_PATH = $pluginDir
    if (-not (Test-GstElement 'mp4mux')) {
        Restore-CallerEnvironment
        throw "mp4mux is not available from '$GStreamerBin' even with GST_PLUGIN_PATH set."
    }
}

foreach ($needed in @('videotestsrc', 'h264parse', 'mp4mux', 'filesink', 'fdsink', 'fakesink')) {
    if (-not (Test-GstElement $needed)) {
        Restore-CallerEnvironment
        throw "Required element '$needed' is missing from the runtime under '$GStreamerBin'."
    }
}

# Encoder candidates, most preferred first, each with the property that actually controls
# the keyframe interval on that element. The property name is NOT uniform: x264enc calls
# it key-int-max, everything else here calls it gop-size. Getting it wrong does not fail
# loudly -- the element just keeps its default GOP, and the measurement below silently
# stops being about the interval it claims to be about.
$encoderCandidates = @(
    [pscustomobject]@{ Name = 'x264enc';          Template = 'x264enc tune=zerolatency bitrate=2000 speed-preset=ultrafast key-int-max={0}' }
    [pscustomobject]@{ Name = 'openh264enc';      Template = 'openh264enc bitrate=2000000 gop-size={0}' }
    [pscustomobject]@{ Name = 'mfh264enc';        Template = 'mfh264enc bitrate=2000 gop-size={0}' }
    [pscustomobject]@{ Name = 'qsvh264enc';       Template = 'qsvh264enc bitrate=2000 gop-size={0}' }
    [pscustomobject]@{ Name = 'nvd3d11h264enc';   Template = 'nvd3d11h264enc bitrate=2000 gop-size={0}' }
    [pscustomobject]@{ Name = 'nvh264enc';        Template = 'nvh264enc bitrate=2000 gop-size={0}' }
    [pscustomobject]@{ Name = 'nvautogpuh264enc'; Template = 'nvautogpuh264enc bitrate=2000 gop-size={0}' }
    [pscustomobject]@{ Name = 'amfh264enc';       Template = 'amfh264enc bitrate=2000 gop-size={0}' }
    [pscustomobject]@{ Name = 'd3d12h264enc';     Template = 'd3d12h264enc bitrate=2000 gop-size={0}' }
)
if ($Encoder) {
    $encoderCandidates = @($encoderCandidates | Where-Object { $_.Name -eq $Encoder })
    if ($encoderCandidates.Count -eq 0) {
        Restore-CallerEnvironment
        throw "-Encoder '$Encoder' is not one of the known candidates."
    }
}

$chosen = $null
foreach ($c in $encoderCandidates) {
    if (Test-GstElement $c.Name) { $chosen = $c; break }
}
if ($null -eq $chosen) {
    Restore-CallerEnvironment
    throw "No usable H.264 encoder found in '$GStreamerBin'."
}
$encoderSpec = [string]::Format($chosen.Template, $GopFrames)
Write-Host "Encoder       : $encoderSpec"
Write-Host ''

if ($Spike) {
    Invoke-Fmp4Spike
    Restore-CallerEnvironment
    exit 0
}

# ---------------------------------------------------------------- run the three cases

$head = "videotestsrc num-buffers=$NumBuffers ! video/x-raw,format=NV12,width=320,height=240,framerate=$Framerate/1 ! $encoderSpec ! h264parse ! mp4mux fragment-duration=$FragmentMs fragment-mode=dash-or-mss"

$fileMp4 = Join-Path $WorkDir 'filesink.mp4'
$fdMp4   = Join-Path $WorkDir 'fdsink.mp4'

Write-Host '== run 1: filesink (seekable reference)'
$run1 = Invoke-GstLaunch -Arguments "$head ! filesink location=filesink.mp4"
Write-Host ("   exit={0} bytes={1}" -f $run1.ExitCode, $(if (Test-Path $fileMp4) { (Get-Item $fileMp4).Length } else { 0 }))

# -q here is not optional: without it gst-launch writes its progress messages to the SAME
# stdout the muxer is writing bytes to, and the captured file is not an MP4 at all.
# It suppresses progress information only -- bus ERROR/WARNING still go to stderr -- but
# run 3 exists so that does not have to be taken on trust.
Write-Host '== run 2: fdsink fd=1 (non-seekable, bytes captured from stdout)'
$run2 = Invoke-GstLaunch -Arguments "-q $head ! fdsink fd=1" -StdOutFile $fdMp4
Write-Host ("   exit={0} bytes={1}" -f $run2.ExitCode, $(if (Test-Path $fdMp4) { (Get-Item $fdMp4).Length } else { 0 }))

Write-Host '== run 3: fakesink (non-seekable, all bus messages visible)'
$run3 = Invoke-GstLaunch -Arguments "$head ! fakesink dump=false"
Write-Host ("   exit={0}" -f $run3.ExitCode)
Write-Host ''

Set-Content -Path (Join-Path $WorkDir 'run1-filesink.stderr.txt') -Value $run1.StdErr -Encoding utf8
Set-Content -Path (Join-Path $WorkDir 'run2-fdsink.stderr.txt')   -Value $run2.StdErr -Encoding utf8
Set-Content -Path (Join-Path $WorkDir 'run3-fakesink.stderr.txt') -Value $run3.StdErr -Encoding utf8
Set-Content -Path (Join-Path $WorkDir 'run3-fakesink.stdout.txt') -Value $run3.StdOut -Encoding utf8

# ---------------------------------------------------------------- analyse

$a1 = $null
$a2 = $null
if ((Test-Path $fileMp4) -and ((Get-Item $fileMp4).Length -gt 0)) { $a1 = Get-Fmp4Analysis -Path $fileMp4 }
if ((Test-Path $fdMp4)   -and ((Get-Item $fdMp4).Length   -gt 0)) { $a2 = Get-Fmp4Analysis -Path $fdMp4 }

# ---------------------------------------------------------------- judgments

# (1) A file with 0 or 1 fragment makes "every moof starts with a sync sample" trivially
#     true, so fragmentation not engaging must read as a FAILURE, not as a pass.
$j1 = $false
$j1Why = 'no MP4 was produced'
if ($null -ne $a1) {
    $n = $a1.Fragments.Count
    if ($n -lt 2) {
        $j1Why = "only $n moof box(es) -- fragmentation did not engage, so the question was never tested"
    } else {
        $bad = @($a1.Fragments | Where-Object { -not $_.FirstIsSync })
        $noTrun = @($a1.Fragments | Where-Object { $null -eq $_.SampleCount })
        if ($noTrun.Count -gt 0) {
            $j1Why = "$($noTrun.Count) of $n fragments have no readable trun"
        } elseif ($bad.Count -gt 0) {
            $j1Why = "$($bad.Count) of $n fragments start with a NON-sync sample (index: " +
                     (($bad | ForEach-Object { $_.Index }) -join ', ') + ')'
        } else {
            $j1 = $true
            $j1Why = "all $n fragments start with a sync sample"
        }
    }
}

# (2) The initialization segment must come first and must declare fragmentation (mvex).
$j2 = $false
$j2Why = 'no MP4 was produced'
if ($null -ne $a1) {
    if ($a1.FirstMoofIndex -lt 0) {
        $j2Why = 'there is no moof at all'
    } elseif ($a1.FtypIndex -lt 0) {
        $j2Why = 'no ftyp'
    } elseif ($a1.MoovIndex -lt 0) {
        $j2Why = 'no moov'
    } elseif (-not ($a1.FtypIndex -lt $a1.MoovIndex -and $a1.MoovIndex -lt $a1.FirstMoofIndex)) {
        $j2Why = "top-level order is " + (($a1.TopLevelOrder | Select-Object -First 6) -join ', ') + ' ...'
    } elseif (-not $a1.HasMvex) {
        $j2Why = 'moov precedes the first moof but contains no mvex'
    } elseif (-not $a1.HasAvcC) {
        $j2Why = "moov has mvex but the sample entry is '$($a1.SampleFormat)' with no avcC"
    } else {
        $j2 = $true
        $j2Why = "ftyp, then moov (mvex present, sample entry $($a1.SampleFormat)/avcC), then the first moof"
    }
}

# (3) Non-seekable: the run must exit 0, say nothing on the bus, AND the bytes it emitted
#     must carry the same media and the same fragmentation as the seekable reference.
#     Checking only stderr would pass a muxer that silently emitted a broken stream.
#
# What is compared, and why it is not simply "the two files are identical":
#
#   - Everything up to and including the LAST mdat must match exactly (box order, the
#     per-fragment table, and the SHA256 of the concatenated mdat payloads). This is the
#     entire part of the stream a live consumer ever receives, so any difference here is
#     a real defect on the non-seekable path.
#   - Boxes AFTER the last mdat are written at EOS only, and they legitimately differ:
#     with a seekable sink mp4mux seeks back and patches the front moov's duration in
#     place, while a non-seekable sink cannot be rewound, so the front moov keeps
#     duration=0 and the finalised moov is appended at the end instead. Only 'mfra' and
#     'moov' are tolerated there -- anything else, or a shorter stream, still fails.
#
# Requiring byte equality here would report a working live path as broken; not comparing
# the trailing boxes at all would hide a muxer that stopped emitting them.
$msg2 = @(Get-MessageLines $run2.StdErr)
$msg3 = @(Get-MessageLines ($run3.StdErr + "`n" + $run3.StdOut))
$streamingMatches   = $false
$fragmentsMatch     = $false
$mediaMatches       = $false
$trailingTolerated  = $false
$trailingNote       = ''
if (($null -ne $a1) -and ($null -ne $a2)) {
    $streamingMatches = (($a1.StreamingOrder -join ',') -eq ($a2.StreamingOrder -join ','))
    $mediaMatches     = ($a1.MdatSha256 -eq $a2.MdatSha256)
    if ($a1.Fragments.Count -eq $a2.Fragments.Count) {
        $fragmentsMatch = $true
        for ($i = 0; $i -lt $a1.Fragments.Count; $i++) {
            $x = $a1.Fragments[$i]; $y = $a2.Fragments[$i]
            if (($x.BaseMediaDecodeTime -ne $y.BaseMediaDecodeTime) -or
                ($x.SampleCount -ne $y.SampleCount) -or
                ($x.FirstIsSync -ne $y.FirstIsSync)) { $fragmentsMatch = $false; break }
        }
    }
    $unexpected = @($a2.TrailingOrder | Where-Object { $_ -notin @('mfra', 'moov') })
    $trailingTolerated = ($unexpected.Count -eq 0)
    if (($a1.TrailingOrder -join ',') -ne ($a2.TrailingOrder -join ',')) {
        $trailingNote = "trailing boxes differ: filesink [$($a1.TrailingOrder -join ', ')] vs fdsink [$($a2.TrailingOrder -join ', ')] (EOS finalisation only)"
    }
}
$j3 = $false
$j3Why = ''
if ($run2.ExitCode -ne 0) {
    $j3Why = "the fdsink run exited $($run2.ExitCode)"
} elseif ($run3.ExitCode -ne 0) {
    $j3Why = "the fakesink run exited $($run3.ExitCode)"
} elseif ($msg2.Count -gt 0) {
    $j3Why = "the fdsink run printed: " + ($msg2 -join ' | ')
} elseif ($msg3.Count -gt 0) {
    $j3Why = "the fakesink run printed: " + ($msg3 -join ' | ')
} elseif ($null -eq $a2) {
    $j3Why = 'the fdsink run produced no parseable bytes'
} elseif (-not $streamingMatches) {
    $j3Why = 'the fdsink box order up to the last mdat differs from the filesink output'
} elseif (-not $fragmentsMatch) {
    $j3Why = 'the fdsink fragments differ from the filesink fragments (tfdt / sample_count / first-sync)'
} elseif (-not $mediaMatches) {
    $j3Why = 'the concatenated mdat payloads differ between the two sinks'
} elseif (-not $trailingTolerated) {
    $j3Why = "unexpected trailing boxes after the last mdat: $($a2.TrailingOrder -join ', ')"
} else {
    $j3 = $true
    $j3Why = 'both non-seekable runs exited 0 with no WARNING/ERROR; identical media, fragmentation and box order through the last mdat'
    if ($trailingNote) { $j3Why = "$j3Why; $trailingNote" }
}

$bytesIdentical = $false
if ((Test-Path $fileMp4) -and (Test-Path $fdMp4)) {
    $h1 = (Get-FileHash $fileMp4 -Algorithm SHA256).Hash
    $h2 = (Get-FileHash $fdMp4   -Algorithm SHA256).Hash
    $bytesIdentical = ($h1 -eq $h2)
}

# ---------------------------------------------------------------- report

$reportPath = Join-Path $WorkDir 'fmp4-fragment-report.md'
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('# fMP4 fragment verification report (mp4mux)')
$null = $sb.AppendLine()
$null = $sb.AppendLine("- Machine: $env:COMPUTERNAME")
$null = $sb.AppendLine("- GStreamer bin: ``$GStreamerBin``")
$null = $sb.AppendLine("- Version: $versionLine")
$null = $sb.AppendLine("- Encoder: ``$encoderSpec``")
$null = $sb.AppendLine("- Source: ``videotestsrc num-buffers=$NumBuffers`` at ${Framerate}fps, GOP $GopFrames frames")
$null = $sb.AppendLine("- Muxer: ``mp4mux fragment-duration=$FragmentMs fragment-mode=dash-or-mss``")
$null = $sb.AppendLine()
$null = $sb.AppendLine('## Judgments')
$null = $sb.AppendLine()
$null = $sb.AppendLine('| # | Question | Result | Evidence |')
$null = $sb.AppendLine('|---|---|---|---|')
$null = $sb.AppendLine("| 1 | all moof start with sync sample | $(if ($j1) { 'PASS' } else { '**FAIL**' }) | $j1Why |")
$null = $sb.AppendLine("| 2 | ftyp + moov(mvex) precede first moof | $(if ($j2) { 'PASS' } else { '**FAIL**' }) | $j2Why |")
$null = $sb.AppendLine("| 3 | non-seekable sink clean | $(if ($j3) { 'PASS' } else { '**FAIL**' }) | $j3Why |")
$null = $sb.AppendLine()

if ($null -ne $a1) {
    $null = $sb.AppendLine('## Container (filesink run)')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("- Bytes: $($a1.Bytes)")
    $null = $sb.AppendLine("- Top-level box order: ``$(($a1.TopLevelOrder | Select-Object -First 12) -join ', ')`` ($($a1.TopLevelOrder.Count) boxes)")
    $null = $sb.AppendLine("- Boxes after the last mdat (written at EOS): ``$(if ($a1.TrailingOrder.Count) { $a1.TrailingOrder -join ', ' } else { '(none)' })``")
    $null = $sb.AppendLine("- moov contains mvex: $($a1.HasMvex)")
    $null = $sb.AppendLine("- Sample entry: ``$($a1.SampleFormat)``, avcC present: $($a1.HasAvcC)")
    $null = $sb.AppendLine("- Media timescale: $($a1.Timescale)")
    $null = $sb.AppendLine("- trex default_sample_flags: $(if ($null -ne $a1.TrexFlags) { '0x{0:X8}' -f [uint32]$a1.TrexFlags } else { '(absent)' })")
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('## Fragments (filesink run)')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('| moof | tfdt | tfdt (ms) | delta (ms) | sample_count | first sample flags | flags read from | first is sync | depends_on |')
    $null = $sb.AppendLine('|---|---|---|---|---|---|---|---|---|')
    foreach ($f in $a1.Fragments) {
        $null = $sb.AppendLine(('| {0} | {1} | {2} | {3} | {4} | `{5}` | {6} | {7} | {8} |' -f `
            $f.Index, $f.BaseMediaDecodeTime, $f.TfdtMs, $(if ($null -ne $f.DeltaMs) { $f.DeltaMs } else { '-' }),
            $f.SampleCount, $f.FirstSampleFlags, $f.FlagsSource, $f.FirstIsSync, $f.DependsOn))
    }
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('`depends_on` 2 = this sample depends on nothing (an I-frame); 1 = it depends on others; 0 = unknown.')
}

if ($null -ne $a2) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('## Non-seekable run (fdsink fd=1)')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("- Bytes: $($a2.Bytes)")
    $null = $sb.AppendLine("- Box order through the last mdat identical to filesink: $streamingMatches")
    $null = $sb.AppendLine("- Per-fragment table identical to filesink (tfdt / sample_count / first-sync): $fragmentsMatch")
    $null = $sb.AppendLine("- SHA256 of the concatenated mdat payloads identical to filesink: $mediaMatches")
    $null = $sb.AppendLine("- Boxes after the last mdat: filesink ``$(if ($a1.TrailingOrder.Count) { $a1.TrailingOrder -join ', ' } else { '(none)' })`` vs fdsink ``$(if ($a2.TrailingOrder.Count) { $a2.TrailingOrder -join ', ' } else { '(none)' })``")
    $null = $sb.AppendLine("- Byte-for-byte identical to the filesink output: $bytesIdentical")
    $null = $sb.AppendLine("- moov contains mvex: $($a2.HasMvex); fragments: $($a2.Fragments.Count)")
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('A non-seekable sink cannot be rewound, so mp4mux cannot patch the front moov''s')
    $null = $sb.AppendLine('duration in place at EOS. It appends the finalised moov after mfra instead and')
    $null = $sb.AppendLine('leaves the leading one with duration=0. Both are written only at EOS, so a live')
    $null = $sb.AppendLine('consumer never sees either -- and the leading moov, which IS the initialization')
    $null = $sb.AppendLine('segment, is emitted before the first moof in both cases.')
}

$null = $sb.AppendLine()
$null = $sb.AppendLine('## stderr')
foreach ($pair in @(
    @('run 1 - filesink (progress messages enabled)', $run1),
    @('run 2 - fdsink -q', $run2),
    @('run 3 - fakesink (progress messages enabled)', $run3))) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("### $($pair[0]) -- exit $($pair[1].ExitCode)")
    $null = $sb.AppendLine('```')
    $t = $pair[1].StdErr
    if (-not $t) { $t = '(empty)' }
    $null = $sb.AppendLine($t.Trim())
    $null = $sb.AppendLine('```')
}

Set-Content -Path $reportPath -Value $sb.ToString() -Encoding utf8

# ---------------------------------------------------------------- console summary

if ($null -ne $a1) {
    Write-Host "Top-level boxes : $(($a1.TopLevelOrder | Select-Object -First 12) -join ', ') ($($a1.TopLevelOrder.Count) total)"
    Write-Host "moov has mvex   : $($a1.HasMvex)   sample entry: $($a1.SampleFormat)   avcC: $($a1.HasAvcC)   timescale: $($a1.Timescale)"
    Write-Host ''
    $a1.Fragments |
        Select-Object Index, BaseMediaDecodeTime, TfdtMs, DeltaMs, SampleCount, FirstSampleFlags,
                      FlagsSource, FirstIsSync, DependsOn |
        Format-Table -AutoSize
}

Write-Host '---------------------------------------------------------------'
Write-Host ("(1) all moof start with sync sample      : {0}  -- {1}" -f $(if ($j1) { 'PASS' } else { 'FAIL' }), $j1Why)
Write-Host ("(2) ftyp+moov(mvex) precede first moof   : {0}  -- {1}" -f $(if ($j2) { 'PASS' } else { 'FAIL' }), $j2Why)
Write-Host ("(3) non-seekable sink clean             : {0}  -- {1}" -f $(if ($j3) { 'PASS' } else { 'FAIL' }), $j3Why)
Write-Host '---------------------------------------------------------------'
Write-Host "Report written to: $reportPath"

$failures = 0
if (-not $j1) { $failures++ }
if (-not $j2) { $failures++ }
if (-not $j3) { $failures++ }

if (-not $KeepWorkDir -and $failures -eq 0) {
    Copy-Item $reportPath (Join-Path ([System.IO.Path]::GetTempPath()) 'fmp4-fragment-report.md') -Force
    Remove-Item -Recurse -Force $WorkDir
    Write-Host "Work dir removed (report copied to %TEMP%\fmp4-fragment-report.md). Use -KeepWorkDir to retain artefacts."
}

Restore-CallerEnvironment

exit $(if ($failures -gt 0) { 1 } else { 0 })
