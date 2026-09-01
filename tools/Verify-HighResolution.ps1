<#
.SYNOPSIS
    Unattended verification that high-resolution recording works (issue N1: at 4K the
    recorder reported IsInitialized=on / LastError=null yet neither recorded nor
    previewed a single frame).

.DESCRIPTION
    Run this on the machine that showed the problem (Intel + NVIDIA, 4K monitor).
    It needs no manual steps and no GUI interaction: it creates an isolated data
    directory, writes settings.json per case, starts the resident worker, records,
    validates the MP4 by parsing ISO-BMFF directly, and writes a markdown report.

    WHAT WENT WRONG (so the report can be read without the commit message):
      The preview branch queue was left at its default max-size-bytes (10485760).
      A 4K frame is 12-13 MB, and a queue accepts its first buffer even when that
      buffer is over the limit -- so the queue could only ever hold ONE frame. The
      preview appsink is blocked in preroll while the pipeline is PAUSED, so the
      queue never drained, the full queue blocked the tee, the encoder starved, it
      produced no output, the recording appsink never prerolled, the pipeline never
      reached PLAYING, and the preview stayed blocked. A circular wait.

      Two things were changed: the preview queue is now leaky and unbounded in bytes
      and time, and initialisation now waits for the pipeline to actually reach
      PLAYING instead of treating SetState's ASYNC return as success.

    WHAT THIS SCRIPT PROVES:
      1. The exact reported configuration records again.
      2. The resolution threshold is gone -- 320x240 through 3840x2160 all work with
         the same encoder line, using d3d12testsrc so the result does not depend on
         this machine's monitor layout.
      3. The new PLAYING wait does not reject configurations that used to work
         (every previously-passing case must still be OK).

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless
    the file has a BOM, and non-ASCII literals in a script that gets copied between
    machines are a reliable way to break it.

.PARAMETER PublishDir
    The published application directory (output of
    'dotnet publish -p:PublishProfile=win-x64-aot' -- the shipped form is Native AOT).
    Defaults to the repo's AOT publish output relative to this script.

.PARAMETER MonitorIndex
    Monitor index for the screen-capture case. The original report used 1.
    An out-of-range index makes the state change fail outright (IsInitialized=false),
    which is a different and clearly distinguishable failure.

.EXAMPLE
    .\Verify-HighResolution.ps1
    .\Verify-HighResolution.ps1 -PublishDir D:\pra\publish -KeepWorkDir

.OUTPUTS
    A markdown report at <WorkDir>\high-resolution-report.md and the same summary on
    stdout. Exit code 0 if every case behaved as expected, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$WorkDir,
    [int]$RecordSeconds = 4,
    [int]$MonitorIndex = 1,
    [int]$ContinuousSegmentSeconds = 5,
    # How many segments a continuous case must produce before it moves on. The default (2)
    # only proves that the split happens at all. Raise it to soak the rotation -- e.g.
    # -ContinuousMinSegments 20 -ContinuousWaitSeconds 180 runs each continuous case for
    # about 20 x ContinuousSegmentSeconds.
    [int]$ContinuousMinSegments = 2,
    # Upper bound on that wait. Reaching it without the requested number of segments FAILS
    # the case: a soak that quietly stopped short would otherwise look identical to one
    # that succeeded.
    [int]$ContinuousWaitSeconds = 45,
    # Add rows measured with a real camera (e.g. 'HD Pro Webcam C920'). Empty skips them.
    [string]$CameraName = '',
    # Regex over the case name; only matching rows run (e.g. -CaseFilter '^fps4'). A full
    # run takes about 15 minutes, so this is how the next step of an investigation is re-run alone.
    [string]$CaseFilter = '',
    [switch]$SmokeTest,
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- setup

if (-not $PublishDir) {
    $PublishDir = Join-Path $PSScriptRoot '..\src\ProcessRecorderApp\bin\Release\win-x64\publish\aot'
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
$exe = Join-Path $PublishDir 'ProcessRecorderApp.exe'
if (-not (Test-Path $exe)) {
    throw "ProcessRecorderApp.exe not found under '$PublishDir'. Run 'dotnet publish -p:PublishProfile=win-x64-aot' first, or pass -PublishDir."
}

# Guard against the single most likely way to waste a trip to this machine: running the
# OLD binaries. The fix puts a literal string into the assembly, so its presence is a
# direct, unambiguous check -- much better than trusting a timestamp.
$fixMarker = 'leaky=downstream'

# The continuous-recording branch has its own queue with a different bound, so this
# literal is present only in a build that has the feature. Without it the continuous rows
# below would fail for the boring reason (old binaries) and look like a product defect.
$continuousMarker = 'max-size-buffers=8'

# BOTH publish shapes must be searched. The literal lives in the managed assembly's user
# string heap for a framework/selfcontained publish, and inside the native image for a
# Native AOT publish -- where GStreamer.dll does not exist at all. Looking only at
# GStreamer.dll reports "old build" for every AOT publish, which is the shipped form and
# this script's own default. Measured on both.
function Test-Marker {
    param([string]$Marker)

    foreach ($name in 'GStreamer.dll', 'ProcessRecorderApp.exe') {
        $path = Join-Path $PublishDir $name
        if (-not (Test-Path $path)) { continue }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        # Decode the whole file and use IndexOf rather than a byte loop -- a hand-rolled
        # scan in PowerShell 5.1 is slow enough to notice.
        #
        # BOTH byte alignments must be tried. The string does not start at an even file
        # offset, so decoding from byte 0 splits every UTF-16 code unit across the wrong
        # pair and finds nothing. Measured: the marker sits at odd alignment in the
        # current build, so an offset-0-only check reports "old build" for a correctly
        # fixed one -- which is exactly the false negative that would send someone
        # chasing a phantom.
        foreach ($enc in @([System.Text.Encoding]::Unicode, [System.Text.Encoding]::UTF8)) {
            foreach ($offset in 0, 1) {
                if ($offset -ge $bytes.Length) { continue }
                $text = $enc.GetString($bytes, $offset, $bytes.Length - $offset)
                if ($text.IndexOf($Marker, [System.StringComparison]::Ordinal) -ge 0) { return $true }
            }
        }
    }
    return $false
}

$hasFix = Test-Marker $fixMarker
$hasContinuous = Test-Marker $continuousMarker

if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pra-hires-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$null = New-Item -ItemType Directory -Force $WorkDir

# Isolate completely from any resident instance already running on this machine: without
# BOTH variables the commands would be forwarded to that instance (and its recordings
# would land in the real user profile).
$env:PROCESSRECORDERAPP_DATA_DIR   = $WorkDir
$env:PROCESSRECORDERAPP_KEY_PREFIX = 'PraHiRes_' + [guid]::NewGuid().ToString('N')

$recDir = Join-Path $WorkDir 'rec'
$dotDir = Join-Path $WorkDir 'dot'
# Where each case's logs are copied. Without this only the LAST case's logs survive.
$logDir = Join-Path $WorkDir 'logs'
$null = New-Item -ItemType Directory -Force $recDir
$null = New-Item -ItemType Directory -Force $dotDir
$null = New-Item -ItemType Directory -Force $logDir

# ---------------------------------------------------------------- helpers

# Kill only the workers that belong to THIS run, identified by the pid each worker wrote
# to the isolated activity.log under $WorkDir. Matching by process name would also kill a
# real resident instance the user keeps running (same rationale as Verify-GpuEncoders.ps1).
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

    # Async reads before waiting: a synchronous ReadToEnd never returns while a hung CLI
    # keeps stdout open (so the timeout would never fire), and deadlocks once the other
    # pipe's buffer fills (same rationale as Verify-GpuEncoders.ps1's Invoke-Cli).
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()

    $timedOut = -not $p.WaitForExit($TimeoutMs)
    if ($timedOut) {
        try { $p.Kill() } catch { }
        $null = $p.WaitForExit(5000)
    }

    $out = ''
    $err = ''
    try { if ($outTask.Wait(2000)) { $out = $outTask.Result } } catch { }
    try { if ($errTask.Wait(2000)) { $err = $errTask.Result } } catch { }

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
        [string]$SrcPipeline,
        [string]$EncodingProperties,    # $null => automatic selection
        [int]$BufferDuration = 3000,
        [bool]$Continuous = $false,     # second, always-on recording on a third tee branch
        [string]$ContinuousFramerate = '',
        [string]$ContinuousResolution = '',
        [string]$ContinuousEnc = '',   # empty => automatic selection (head of EncoderCatalog)
        [int]$ContinuousSegmentSeconds = 5,
        [string]$SecondSrc = '',        # non-empty => add a second recorder
        [string]$SecondType = '',
        [string]$GstDebug = ''        # GStreamer-side logging (e.g. videorate:5)
    )

    $encProp   = if ([string]::IsNullOrEmpty($EncodingProperties)) { 'null' } else { '"' + $EncodingProperties + '"' }
    $tmpl      = ($recDir -replace '\\', '\\') + '\\{Name}_{Now:HHmmssfff}.mp4'
    $logPath   = ($WorkDir -replace '\\', '\\') + '\\debug.log'
    $dotPath   = ($dotDir  -replace '\\', '\\')

    # Continuous recording writes its own segments next to the event recording. The
    # template keeps them apart by name so the two can be counted separately.
    $contTmpl  = ($recDir -replace '\\', '\\') + '\\{Name}_c{Segment}.mp4'
    $contFlag  = if ($Continuous) { 'true' } else { 'false' }
    # The continuous-branch encoder must be nameable: with automatic selection the branch
    # lands on d3d12h264enc even when the event line is qsvh264enc, and TWO DIFFERENT ENGINES CANNOT CONTEND.
    $contEnc   = if ([string]::IsNullOrEmpty($ContinuousEnc)) { 'null' } else { '"' + $ContinuousEnc + '"' }

    # settings.json is built by string concatenation, so a SrcPipeline containing " (for
    # example mfvideosrc device-name="...") has to be escaped here or the JSON breaks.
    function ConvertTo-JsonString([string]$value) { return ($value -replace '\\', '\\' -replace '"', '\"') }

    # One recorder definition. This is a function so that TWO recorders can be tested
    # -- the reported setup has a camera and a screen capture recording continuously at the
    # same time, and a single-recorder measurement does not reproduce that condition.
    function New-RecorderJson([string]$name, [string]$type, [string]$src) {
        return @"
    {
      "Name": "$name",
      "BufferDuration": $BufferDuration,
      "FilenameTemplate": "$tmpl",
      "Type": "$type",
      "SrcPipeline": "$(ConvertTo-JsonString $src)",
      "EncodingProperties": $encProp,
      "ContinuousRecording": $contFlag,
      "ContinuousFramerate": "$ContinuousFramerate",
      "ContinuousResolution": "$ContinuousResolution",
      "ContinuousEncodingProperties": $contEnc,
      "ContinuousFilenameTemplate": "$contTmpl",
      "ContinuousSegmentSeconds": $ContinuousSegmentSeconds
    }
"@
    }

    $recorders = New-RecorderJson 'R1' $RecorderType $SrcPipeline
    if (-not [string]::IsNullOrEmpty($SecondSrc)) {
        $secondType = if ([string]::IsNullOrEmpty($SecondType)) { $RecorderType } else { $SecondType }
        $recorders = $recorders + ",`r`n" + (New-RecorderJson 'R2' $secondType $SecondSrc)
    }

    $json = @"
{
  "DataVersion": 1,
  "DebugLogFile": "$logPath",
  "GstDebugDumpDotDir": "$dotPath",
  "GstDebug": "$GstDebug",
  "PreferredH264Encoder": "",
  "Recorders": [
$recorders
  ]
}
"@
    Set-Content -Path (Join-Path $WorkDir 'settings.json') -Value $json -Encoding utf8
}

# Sum of the sample counts of every 'trun' inside one 'moof'. A fragmented MP4 counts NO
# samples in the moov (its stsz sample_count is 0), so the fragments are the only place the
# frame count exists -- and without it the 'event fps' column, which is the whole point of
# the fps rows, reads n/a for every file the product writes by default.
#
# The boxes are WALKED (moof -> traf -> trun), not searched for as text: the four bytes
# 'trun' can occur anywhere inside sample data, and a false hit would read four arbitrary
# bytes as a sample count and inflate the total by millions.
function Get-TrunSampleCount {
    param([byte[]]$Bytes, [int]$Offset, [int]$End)

    $total = 0
    $pos = $Offset
    while ($pos + 8 -le $End) {
        $size = ([uint32]$Bytes[$pos] -shl 24) -bor ([uint32]$Bytes[$pos+1] -shl 16) -bor ([uint32]$Bytes[$pos+2] -shl 8) -bor [uint32]$Bytes[$pos+3]
        $type = [System.Text.Encoding]::ASCII.GetString($Bytes, $pos + 4, 4)
        $header = 8
        if ($size -eq 1) {
            # 64-bit size. Only the low 32 bits are read: no moof is 4GB, and reading the
            # high word would only matter for a file this probe could not walk anyway.
            if ($pos + 16 -gt $End) { break }
            $size = ([uint32]$Bytes[$pos+12] -shl 24) -bor ([uint32]$Bytes[$pos+13] -shl 16) -bor ([uint32]$Bytes[$pos+14] -shl 8) -bor [uint32]$Bytes[$pos+15]
            $header = 16
        }
        # A short or overlong box means the fragment is truncated (the segment that was
        # still open when the workers were killed). Stop rather than walk off the array.
        if ($size -lt $header -or ($pos + $size) -gt $End) { break }

        if ($type -eq 'traf') {
            $total = $total + (Get-TrunSampleCount -Bytes $Bytes -Offset ($pos + $header) -End ($pos + $size))
        }
        elseif ($type -eq 'trun') {
            # trun: size(4) type(4) version(1) flags(3) sample_count(4)
            $c = $pos + $header + 4
            if (($c + 4) -le $End) {
                $total = $total + (([uint32]$Bytes[$c] -shl 24) -bor ([uint32]$Bytes[$c+1] -shl 16) -bor ([uint32]$Bytes[$c+2] -shl 8) -bor [uint32]$Bytes[$c+3])
            }
        }
        $pos = $pos + $size
    }
    return $total
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
            HasAvc1 = $false; DurationSec = $null; FrameCount = $null; EffectiveFps = $null
            MoofCount = 0; DurationSource = 'moov'; FrameCountSource = $null
        }

        # Frames counted in the fragments. Kept apart from $res.FrameCount so the moov
        # stays the preferred source and this is only fallen back to below.
        $fragmentSamples = 0

        while ($fs.Position -lt $fs.Length - 8) {
            $start = $fs.Position
            $b = $br.ReadBytes(4)
            if ($b.Length -lt 4) { break }
            $size = ([uint32]$b[0] -shl 24) -bor ([uint32]$b[1] -shl 16) -bor ([uint32]$b[2] -shl 8) -bor [uint32]$b[3]
            $type = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
            $hdr = 8
            if ($size -eq 1) { $size = [int64]$br.ReadUInt64(); $hdr = 16 }
            if ($size -lt $hdr) { break }

            switch ($type) {
                'ftyp' { $res.HasFtyp = $true }
                'mdat' { $res.HasMdat = $true }
                'moof' {
                    $res.MoofCount = $res.MoofCount + 1
                    # Read the whole moof (a few hundred bytes) and count its samples.
                    $fs.Position = $start + $hdr
                    $moof = $br.ReadBytes([int]($size - $hdr))
                    $fragmentSamples = $fragmentSamples + (Get-TrunSampleCount -Bytes $moof -Offset 0 -End $moof.Length)
                }
                'moov' {
                    $res.HasMoov = $true
                    $null = $br.ReadBytes(4)
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
                    # stsz sample_count = frame count; with duration it gives the effective fps.
                    # READ THE FRAME COUNT, NOT THE NEGOTIATED CAPS FRAMERATE -- the event line
                    # drops while the caps still read 30/1.
                    $txt = [System.Text.Encoding]::ASCII.GetString($moov)
                    $ix = $txt.IndexOf('stsz')
                    if ($ix -ge 0 -and ($ix + 16) -lt $moov.Length) {
                        # stsz: size(4) type(4) ver+flags(4) sample_size(4) sample_count(4)
                        $c = $ix + 12
                        $res.FrameCount = ([uint32]$moov[$c] -shl 24) -bor ([uint32]$moov[$c+1] -shl 16) -bor ([uint32]$moov[$c+2] -shl 8) -bor [uint32]$moov[$c+3]
                        # ONLY when the moov actually counts samples. A fragmented file has
                        # stsz sample_count = 0, and computing 0/duration here would leave a
                        # non-null EffectiveFps of 0 that the fragment count below could no
                        # longer replace -- the report would read '0 (0f)' instead of the
                        # real rate.
                        if ($res.FrameCount -gt 0) {
                            $res.FrameCountSource = 'stsz'
                            if ($res.DurationSec -gt 0) { $res.EffectiveFps = [math]::Round($res.FrameCount / $res.DurationSec, 1) }
                        }
                    }
                }
            }
            $fs.Position = $start + $size
        }

        # A fragmented MP4 -- which FragmentedOutput (default true) produces -- has a zero
        # mvhd duration: the length lives in the fragments and the moov is never rewritten.
        # Prefer the sidecar the app writes beside the recording (<file>.mp4.json), and
        # where there is none, report the length as unknown rather than calling a good
        # recording zero-length.
        if (-not ($res.DurationSec -gt 0)) {
            $res.DurationSource = 'unknown'
            $sidecar = $Path + '.json'
            if (Test-Path $sidecar) {
                try {
                    $meta = [IO.File]::ReadAllText($sidecar, [Text.Encoding]::UTF8) | ConvertFrom-Json
                    if ($meta.durationMs -gt 0) {
                        $res.DurationSec = [math]::Round($meta.durationMs / 1000, 3)
                        $res.DurationSource = 'sidecar'
                    }
                } catch {
                    # Unreadable sidecar = no sidecar. The moof count below is the answer.
                }
            }
            if (($res.DurationSource -ne 'sidecar') -and ($res.MoofCount -ge 1)) {
                $res.DurationSource = 'fragmented'
            }
        }

        # The frame count of a fragmented file. The moov of such a file counts no samples,
        # so without this the 'event fps' column -- the only way to see the event line
        # dropping frames while the negotiated caps still read 30/1 -- is n/a for every
        # recording the product writes with FragmentedOutput on (the default).
        if (-not ($res.FrameCount -gt 0) -and ($fragmentSamples -gt 0)) {
            $res.FrameCount = $fragmentSamples
            $res.FrameCountSource = 'trun'
            # The length comes from the sidecar here (the mvhd of a fragmented file is 0),
            # so a recording written by an older build -- one with no sidecar -- still has
            # no rate to report, only a frame count.
            if ($res.DurationSec -gt 0) { $res.EffectiveFps = [math]::Round($res.FrameCount / $res.DurationSec, 1) }
        }

        return $res
    } finally { $fs.Dispose() }
}

# Whether the length check is satisfied. FRAGMENTED WITHOUT A SIDECAR IS NOT A FAILURE:
# there is no length to read, and the structural checks above already say the file is a
# real MP4 with an H.264 track. Only a non-fragmented file with no length fails here.
function Test-Mp4Duration {
    param($Probe)

    if ($null -eq $Probe) { return $false }
    return ($Probe.DurationSec -gt 0) -or ($Probe.DurationSource -eq 'fragmented')
}

# How the length reads in the console line and the report table.
function Format-Mp4Duration {
    param($Probe)

    if ($null -eq $Probe) { return 'n/a' }
    if ($Probe.DurationSec -gt 0) {
        if ($Probe.DurationSource -eq 'sidecar') { return ('{0}s (sidecar)' -f $Probe.DurationSec) }
        return ('{0}s' -f $Probe.DurationSec)
    }
    if ($Probe.DurationSource -eq 'fragmented') { return ('fragmented ({0} moof)' -f $Probe.MoofCount) }
    return 'n/a'
}

function Get-ActivityLines {
    param([string]$Pattern)
    $log = Join-Path $WorkDir 'activity.log'
    if (-not (Test-Path $log)) { return @() }
    return @(Get-Content $log | Where-Object { $_ -match $Pattern })
}

# ---------------------------------------------------------------- cases

# The exact configuration from the report. This one depends on the machine's monitor
# layout, which is why the sweep below uses d3d12testsrc instead.
$reportedSrc = "d3d12screencapturesrc monitor-index=$MonitorIndex show-cursor=true ! video/x-raw(memory:D3D12Memory), framerate=15/1"

# This is EncoderCatalog's qsv launch string, used for the reported 4K configuration and for
# the sweep too, so that only the resolution differs between rows.
#
# The two must stay equal: if the catalog changes and this does not, the sweep quietly starts
# verifying a configuration the product no longer produces, and still reports green.
# EncoderCatalogScriptSyncTests pins them together, so a deliberate catalog change fails that
# test and forces a decision here rather than drifting silently.
#
# What this script measures (the ring buffer's cyclic wait above the per-frame threshold) does
# not depend on the encoder's rate control, so following the catalog is safe here.
$reportedEnc = 'qsvh264enc rate-control=vbr bitrate=2000 max-bitrate=3000 gop-size=60'

# The bitrate THE PRODUCT WOULD COMPUTE for a 4K source at 15fps
# (EncoderCatalog.BitrateKbpsFor: 3840x2160x15x0.1/1000 -> 12442 kbit/sec, peak 1.5x -> 18663).
# A hand-written encoder string is used as is, so the catalog default (2000/3000) never
# measures what the product actually builds at 4K -- and a unit error (kbit vs bit) stays
# invisible, because 2000 is plausible either way and 12442 is not. This gets its OWN case
# below rather than replacing the sweep row: the sweep's whole value is that nothing but the
# resolution differs between its rows. Pinned by EncoderCatalogScriptSyncTests.
$fourKFormulaEnc = 'qsvh264enc rate-control=vbr bitrate=12442 max-bitrate=18663 gop-size=60'

# For the rows that run the continuous branch at 5fps. A HAND-WRITTEN ENCODER STRING IS USED
# AS IS, so the product's "two seconds derived from the framerate" does not apply. Passing
# $reportedEnc (gop-size=60, which is 30fps based) gives a 12-second keyframe interval at 5fps,
# and a segment can only be split on a keyframe, so a 5-second setting stretches to 10
# (measured; continuous.overshoot). Pin the GOP to the branch rate here instead.
$reportedContinuousEnc = 'qsvh264enc rate-control=vbr bitrate=2000 max-bitrate=3000 gop-size=10'

$cases = New-Object System.Collections.Generic.List[object]

# -SmokeTest exercises every part of this script (settings.json, the CLI round trip, the
# MP4 probe, the activity.log parsing, the report) on a machine with no GPU and no Intel
# Quick Sync, so the script itself can be validated BEFORE it is carried to the machine
# that has the problem. Verifying a GPU fix is a round trip measured in hours; debugging
# the harness remotely is the expensive way to spend it.
if ($SmokeTest) {
    Write-Host 'SMOKE TEST: running one cheap System/videotestsrc case. This does NOT verify N1.'
    Write-Host ''
    $cases.Add([pscustomobject]@{
        Name = 'smoke: System / videotestsrc 320x240 / automatic encoder selection'
        Type = 'System'
        Src  = 'videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420,width=320,height=240,framerate=15/1'
        Enc  = $null; Buffer = 3000
        Note = 'harness self-check only -- proves the script runs, not that N1 is fixed'
        ExpectStall = $false
    })
    # A green harness proves nothing about the red path. identity drop-probability=1.0
    # lets caps through and discards every buffer, so the pipeline links and changes state
    # but never reaches PLAYING -- the same shape as N1, reached deliberately. This case is
    # EXPECTED to fail, and the run is only OK if it fails in exactly that way.
    $cases.Add([pscustomobject]@{
        Name = 'smoke: a source that never delivers a frame (EXPECTED to be reported as stalled)'
        Type = 'System'
        Src  = 'videotestsrc is-live=true do-timestamp=true ! identity drop-probability=1.0 ! videoconvert ! video/x-raw,format=I420,width=320,height=240,framerate=15/1'
        Enc  = 'x264enc'; Buffer = 3000
        Note = 'harness self-check of the FAILURE path -- proves this script can actually see a stall'
        ExpectStall = $true
    })
    # The continuous rows are the reason this script was extended, so the harness for them
    # has to be self-checked too: settings.json shape, segment counting, and the separate
    # continuous verdict. This one runs anywhere (no GPU, no Quick Sync).
    $cases.Add([pscustomobject]@{
        Name = 'smoke: System / videotestsrc 320x240 with a continuous branch at 5fps'
        Type = 'System'
        Src  = 'videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420,width=320,height=240,framerate=15/1'
        Enc  = $null; Buffer = 3000
        Continuous = $true; ContinuousFramerate = '5/1'
        # Naming the continuous encoder and the per-case duration are exercised here too
        # (when writing one by hand, pin the GOP -- key-int-max is that knob).
        ContinuousEnc = 'x264enc tune=zerolatency bitrate=800 speed-preset=ultrafast key-int-max=15'
        Seconds = 6
        Note = 'harness self-check of the continuous half -- segment checks, named continuous encoder, per-case duration'
        ExpectStall = $false
    })
    # A second recorder, and the JSON escaping of a SrcPipeline containing ", are self-checked
    # here as well -- both are only used with a real camera setup, so without this row a
    # breakage would first appear on the real machine and waste a round trip.
    $cases.Add([pscustomobject]@{
        Name = 'smoke: TWO recorders and a quoted SrcPipeline'
        Type = 'System'
        Src  = 'videotestsrc is-live=true do-timestamp=true ! videoconvert ! capsfilter caps="video/x-raw,format=I420,width=320,height=240,framerate=15/1"'
        Enc  = $null; Buffer = 3000
        SecondSrc = 'videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420,width=320,height=240,framerate=15/1'
        SecondType = 'System'
        # The per-case GstDebug override runs here too (it is only used on real hardware).
        GstDebug = 'videotestsrc:5'
        Note = 'harness self-check: the JSON escaping and the second recorder'
        ExpectStall = $false
    })
}
else {

$cases.Add([pscustomobject]@{
    Name = "REPORTED: 4K screen capture, monitor-index=$MonitorIndex, qsvh264enc vbr"
    Type = 'D3d12'; Src = $reportedSrc; Enc = $reportedEnc; Buffer = 10000
    Note = 'the reported configuration: source, monitor index and BufferDuration as received.' +
           ' The encoder line follows EncoderCatalog (pinned by EncoderCatalogScriptSyncTests), so it is' +
           ' what the product builds today, not the string the report carried.'
})

# Resolution sweep. Everything except width/height is identical, so a difference between
# rows can only be the resolution. 1920x1080 and below used to work; 2560x1440 and above
# used to deadlock (measured on a GPU-less dev machine with the same pipeline shape).
foreach ($wh in @('320x240', '1920x1080', '2560x1440', '3840x2160')) {
    $parts = $wh.Split('x')
    $cases.Add([pscustomobject]@{
        Name = "sweep: d3d12testsrc $wh, qsvh264enc vbr"
        Type = 'D3d12'
        Src  = "d3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=$($parts[0]), height=$($parts[1]), framerate=15/1"
        Enc  = $reportedEnc; Buffer = 3000
        Note = if ([int]$parts[0] * [int]$parts[1] * 3 / 2 -gt 5242880) { 'ABOVE the old threshold -- this used to deadlock' } else { 'below the old threshold -- this used to work; must still work' }
    })
}

# Same 4K source as the last sweep row, but with the bitrate the product derives for it.
# Read the MP4 bytes and the duration of this row against the 3840x2160 sweep row: the
# catalog default (2000 kbit/sec) and the formula value (12442) must produce visibly
# different file sizes. If they do not, the value is not reaching the encoder; if this row
# comes out enormously larger than ~12.4 Mbit/sec, the unit is wrong.
$cases.Add([pscustomobject]@{
    Name = 'sweep: d3d12testsrc 3840x2160, qsvh264enc vbr at the derived bitrate'
    Type = 'D3d12'
    Src  = 'd3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=3840, height=2160, framerate=15/1'
    Enc  = $fourKFormulaEnc; Buffer = 3000
    Note = 'the bitrate the product computes for this source (12442/18663 kbit/sec) -- compare bytes and duration with the 3840x2160 sweep row'
})

# Isolating the event-framerate drop. THE THREE ROWS DIFFER ONLY IN THE CONTINUOUS SETTINGS;
# source and event encoder are identical. Compare the effective fps (stsz frame count divided
# by duration) -- THE NEGOTIATED CAPS READ 30/1 ON ALL THREE ROWS, so caps cannot tell them apart.
# On a GPU-less dev machine videorate makes no difference (measured). This needs a GPU.
$fpsSrc = 'd3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=1920, height=1080, framerate=30/1'
$cases.Add([pscustomobject]@{
    Name = 'fps: 1920x1080@30, continuous OFF (baseline)'
    Type = 'D3d12'; Src = $fpsSrc; Enc = $reportedEnc; Buffer = 3000
    Note = 'the event line on its own -- the number the other two rows must match'
})
$cases.Add([pscustomobject]@{
    Name = 'fps: 1920x1080@30, continuous ON, no framerate override'
    Type = 'D3d12'; Src = $fpsSrc; Enc = $reportedEnc; Buffer = 3000
    Continuous = $true; ContinuousResolution = '960x540'
    Note = 'three branches and a second encoder, but no videorate'
})
$cases.Add([pscustomobject]@{
    Name = 'fps: 1920x1080@30, continuous ON at 5fps (videorate)'
    Type = 'D3d12'; Src = $fpsSrc; Enc = $reportedEnc; Buffer = 3000
    Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '960x540'
    Note = 'the reported configuration -- the event MP4 came out at about 12fps'
})

# The three rows above (d3d12testsrc + automatic continuous encoder) all measured 30fps, so
# videorate alone does not drop the event line. Two differences from the reported setup remain:
#   (a) the source is system memory (mfvideosrc) and the product inserts d3d12upload
#   (b) the continuous branch is qsvh264enc too (automatic selection picks d3d12h264enc,
#       a different engine, which does not contend). Each row below changes EXACTLY ONE FACTOR,
# so the difference from the preceding row is the cause. Widen the window for this group only.
$fpsSrcD3d12 = 'd3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=1920, height=1080, framerate=30/1'
$fpsSrcSystem = 'videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw, format=NV12, width=1920, height=1080, framerate=30/1'
$fpsSeconds = 20

$cases.Add([pscustomobject]@{
    Name = 'fps2: D3D12 src, continuous 5fps, continuous encoder = qsvh264enc'
    Type = 'D3d12'; Src = $fpsSrcD3d12; Enc = $reportedEnc; Buffer = 3000; Seconds = $fpsSeconds
    Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '960x540'
    ContinuousEnc = $reportedContinuousEnc
    Note = 'only change from round 1 row 3: the continuous branch is on Quick Sync too (two QSV sessions)'
})
$cases.Add([pscustomobject]@{
    Name = 'fps2: SYSTEM-memory src, continuous OFF (baseline for the rows below)'
    Type = 'D3d12'; Src = $fpsSrcSystem; Enc = $reportedEnc; Buffer = 3000; Seconds = $fpsSeconds
    Note = 'the product inserts d3d12upload for a system-memory source, as it does for mfvideosrc'
})
$cases.Add([pscustomobject]@{
    Name = 'fps2: SYSTEM-memory src, continuous 5fps, continuous encoder = auto'
    Type = 'D3d12'; Src = $fpsSrcSystem; Enc = $reportedEnc; Buffer = 3000; Seconds = $fpsSeconds
    Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '960x540'
    Note = 'adds videorate on top of the upload path'
})
$cases.Add([pscustomobject]@{
    Name = 'fps2: SYSTEM-memory src, continuous 5fps, continuous encoder = qsvh264enc'
    Type = 'D3d12'; Src = $fpsSrcSystem; Enc = $reportedEnc; Buffer = 3000; Seconds = $fpsSeconds
    Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '960x540'
    ContinuousEnc = $reportedContinuousEnc
    Note = 'closest to the reported configuration: upload path + videorate + two QSV sessions'
})
$cases.Add([pscustomobject]@{
    Name = 'fps2: SYSTEM-memory src, continuous WITHOUT framerate, continuous encoder = qsvh264enc'
    Type = 'D3d12'; Src = $fpsSrcSystem; Enc = $reportedEnc; Buffer = 3000; Seconds = $fpsSeconds
    Continuous = $true; ContinuousResolution = '960x540'
    ContinuousEnc = $reportedEnc
    Note = 'the same but with the framerate limit off -- the reported working case'
})

# Synthetic sources stay at 30fps whatever is done to them (upload path, two QSV sessions,
# videorate, a 20-second window -- none of them reproduce it). Two differences from the
# reported setup are left:
#   (a) the source is a real camera (mfvideosrc; MF is decoding MJPEG)
#   (b) TWO RECORDERS AT ONCE, both with continuous recording on (= four sessions)
# These run only when -CameraName is given (skipped on a machine without that camera).
if (-not [string]::IsNullOrEmpty($CameraName)) {
    $camSrc = "mfvideosrc device-name=`"$CameraName`" ! video/x-raw, format=NV12, width=1920, height=1080, framerate=30/1"
    $camSeconds = 20

    $cases.Add([pscustomobject]@{
        Name = "fps3: camera '$CameraName' 1080p30, continuous OFF"
        Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds
        Note = 'the real camera on its own -- if this is already below 30 the camera is the ceiling'
    })
    $cases.Add([pscustomobject]@{
        Name = "fps3: camera, continuous WITHOUT framerate, continuous encoder = qsvh264enc"
        Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds
        Continuous = $true; ContinuousResolution = '960x540'; ContinuousEnc = $reportedEnc
        Note = 'the reported WORKING case'
    })
    $cases.Add([pscustomobject]@{
        Name = "fps3: camera, continuous 5fps, continuous encoder = qsvh264enc"
        Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds
        Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '960x540'
        ContinuousEnc = $reportedContinuousEnc
        Note = 'the reported FAILING case -- one recorder only'
    })
    # THIS ROW ALONE LEAVES ContinuousResolution OFF. The continuous settings are shared by
    # every recorder, and the second recorder (screen capture) has no width/height in its caps
    # -- the product drops the override and writes recorder.continuous-init fail (src/README.md),
    # which this harness always counts as a failure (fail must be 0). What this row is for is
    # FOUR SIMULTANEOUS SESSIONS, not scaling, so the override is left off. Scaling is covered
    # by the single-recorder rows above (a camera with fixed caps).
    $cases.Add([pscustomobject]@{
        Name = "fps3: camera + screen capture (TWO recorders), both continuous 5fps"
        Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds
        Continuous = $true; ContinuousFramerate = '5/1'
        ContinuousEnc = $reportedContinuousEnc
        SecondSrc = $reportedSrc; SecondType = 'D3d12'
        Note = 'the full reported setup -- four encoder sessions at once (event x2 + continuous x2). event fps is R1 (the camera)'
    })

    # drop-only=true did not fix it (11.9 -> 12.4fps), so "videorate holds the previous buffer
    # and keeps hold of the pool" is out. What is left to separate is whether videorate merely
    # BEING PRESENT matters, or LOWERING THE RATE does:
    #   30/1 = same as the source -> videorate is present but converts nothing
    #   15/1 = half               -> does the loss track the ratio?
    #   5/1 with no downscale     -> is the scaler involved?
    # The last row collects GStreamer-side logs (videorate drops and queue-full messages).
    $camSeconds4 = 20
    foreach ($r in @('30/1', '15/1')) {
        $cases.Add([pscustomobject]@{
            Name = "fps4: camera, continuous $r (videorate present)"
            Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds4
            Continuous = $true; ContinuousFramerate = $r; ContinuousResolution = '960x540'
            ContinuousEnc = $reportedEnc
            Note = if ($r -eq '30/1') { 'videorate is present but changes nothing -- if this drops, the element itself is the trigger' } else { 'half rate -- does the loss track the ratio?' }
        })
    }
    $cases.Add([pscustomobject]@{
        Name = 'fps4: camera, continuous 5fps, NO resolution override'
        Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds4
        Continuous = $true; ContinuousFramerate = '5/1'
        ContinuousEnc = $reportedContinuousEnc
        Note = 'videorate only, no scaler in the branch'
    })
    $cases.Add([pscustomobject]@{
        Name = 'fps4: camera, continuous 5fps, with GStreamer logging'
        Type = 'D3d12'; Src = $camSrc; Enc = $reportedEnc; Buffer = 3000; Seconds = $camSeconds4
        Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '960x540'
        ContinuousEnc = $reportedContinuousEnc
        GstDebug = 'videorate:5,queue:4,mfvideosrc:4'
        Note = 'same as the failing row but with GST_DEBUG -- read debug.log for videorate drops and queue-full messages'
    })

}

# Automatic selection at 4K exercises the candidate fallback with the new PLAYING wait in
# the loop: if a candidate stalls it must be rejected and the next one tried.
$cases.Add([pscustomobject]@{
    Name = 'sweep: d3d12testsrc 3840x2160, automatic encoder selection'
    Type = 'D3d12'
    Src  = 'd3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=3840, height=2160, framerate=15/1'
    Enc  = $null; Buffer = 3000
    Note = 'candidate fallback at 4K, now that a stalled candidate is rejected instead of accepted'
})

# ---- continuous recording -------------------------------------------------------------
#
# Continuous recording adds a THIRD branch to the same tee (a second encoder, optionally a
# videorate and a scaler, ending in an appsink). The measurements that produced the
# leaky preview queue were taken with TWO branches, so the resolution sweep has to be
# repeated with three -- that is the whole point of running this on the real machine.
#
# What could go wrong and is invisible without a GPU:
#   * a third consumer of the tee slows every branch enough that the recording appsink
#     no longer prerolls inside PlayingStateTimeoutMs -> "never reached PLAYING",
#   * the continuous branch itself stalls and takes the pipeline down with it (it must
#     not: its queue is leaky and its appsink is async=false),
#   * the second encoder cannot be created at 4K (GPU session/memory limits).
#
# The event recording is checked exactly as in the rows above; the continuous side is
# checked separately (segments written, each one a valid MP4) so a failure says which
# half broke.
foreach ($wh in @('1920x1080', '2560x1440', '3840x2160')) {
    $parts = $wh.Split('x')
    $cases.Add([pscustomobject]@{
        Name = "continuous: d3d12testsrc $wh, event + always-on branch, same framerate"
        Type = 'D3d12'
        Src  = "d3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=$($parts[0]), height=$($parts[1]), framerate=15/1"
        Enc  = $reportedEnc; Buffer = 3000
        Continuous = $true
        Note = 'three tee branches at this resolution -- the two-branch measurements do not cover this'
    })
}

# Different frame rate AND a smaller frame size on the continuous branch: this is the
# shape the feature exists for (a light always-on archive next to the event recording).
# It is also the only row that exercises videorate, which lives in the bundled runtime
# but not necessarily in a separately installed GStreamer.
$cases.Add([pscustomobject]@{
    Name = 'continuous: d3d12testsrc 3840x2160 event + 5fps 1280x720 always-on branch'
    Type = 'D3d12'
    Src  = 'd3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=3840, height=2160, framerate=15/1'
    Enc  = $reportedEnc; Buffer = 3000
    Continuous = $true; ContinuousFramerate = '5/1'; ContinuousResolution = '1280x720'
    Note = 'videorate + scaler on the third branch, which is the intended production shape'
})

# Screen capture with the continuous branch, at whatever the monitor actually is. The
# rows above use d3d12testsrc so they do not depend on the monitor layout; this one does,
# and that is exactly why it is worth one row.
$cases.Add([pscustomobject]@{
    Name = "continuous: REPORTED screen capture, monitor-index=$MonitorIndex, event + 5fps always-on branch"
    Type = 'D3d12'; Src = $reportedSrc; Enc = $reportedEnc; Buffer = 10000
    Continuous = $true; ContinuousFramerate = '5/1'
    Note = 'the real capture source with three branches (monitor-layout dependent)'
})

}   # end of the non-smoke case list

# ---------------------------------------------------------------- run

Write-Host "Publish dir : $PublishDir"
Write-Host "Work dir    : $WorkDir"
Write-Host ("Fixed build : " + $(if ($hasFix) { 'YES (found the leaky preview queue)' } else { 'NO -- THIS LOOKS LIKE AN OLD BUILD' }))
Write-Host ("Continuous  : " + $(if ($hasContinuous) { 'YES (this build has the continuous-recording branch)' } else { 'NO -- continuous rows will fail on old binaries' }))
if (-not $hasFix) {
    Write-Warning "Neither GStreamer.dll nor ProcessRecorderApp.exe contains the fix marker '$fixMarker'."
    Write-Warning "You are probably running binaries from before the N1 fix. Copy the new publish output and re-run."
}
if (-not $hasContinuous) {
    Write-Warning "The build does not contain the continuous-recording marker '$continuousMarker'."
    Write-Warning "The continuous rows below cannot pass. Copy a newer publish output and re-run."
}
Write-Host ''

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param([object]$Case)

    Stop-AllWorkers
    Get-ChildItem $recDir -Filter *.mp4 -ErrorAction SilentlyContinue | Remove-Item -Force

    # COPY THE PREVIOUS CASE'S LOGS BEFORE DELETING THEM. activity.log / debug.log are cleared
    # per case, so without the copy ONLY THE LAST CASE SURVIVES -- the logs of the case that
    # collected GST_DEBUG would be overwritten by later cases and the round trip wasted.
    if ($script:lastCaseName) {
        $safe = ($script:lastCaseName -replace '[^A-Za-z0-9]+', '-').Trim('-')
        if ($safe.Length -gt 60) { $safe = $safe.Substring(0, 60) }
        foreach ($log in 'activity.log', 'debug.log') {
            $src = Join-Path $WorkDir $log
            if (Test-Path $src) { Copy-Item $src (Join-Path $logDir "$safe.$log") -Force }
        }
    }
    $script:lastCaseName = $Case.Name

    Remove-Item (Join-Path $WorkDir 'activity.log') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $WorkDir 'debug.log')    -Force -ErrorAction SilentlyContinue

    $isContinuous = [bool]$Case.Continuous
    # A case with a second recorder emits the init signature twice. Requiring exactly one always fails.
    $expectedRecorders = if ([string]::IsNullOrEmpty([string]$Case.SecondSrc)) { 1 } else { 2 }
    Write-Settings -RecorderType $Case.Type -SrcPipeline $Case.Src `
                   -EncodingProperties $Case.Enc -BufferDuration $Case.Buffer `
                   -Continuous $isContinuous `
                   -ContinuousFramerate ([string]$Case.ContinuousFramerate) `
                   -ContinuousResolution ([string]$Case.ContinuousResolution) `
                   -ContinuousEnc ([string]$Case.ContinuousEnc) `
                   -ContinuousSegmentSeconds $ContinuousSegmentSeconds `
                   -SecondSrc ([string]$Case.SecondSrc) -SecondType ([string]$Case.SecondType) `
                   -GstDebug ([string]$Case.GstDebug)

    # The very first launch on a machine builds the GStreamer plugin registry, which can
    # take longer than the launcher's wait. Retry once.
    $ping = Invoke-Cli 'ping'
    if ($ping.ExitCode -ne 0) {
        Write-Host '   (first launch timed out building the plugin registry; retrying)'
        Start-Sleep -Seconds 3
        $ping = Invoke-Cli 'ping'
    }

    Start-Sleep -Seconds 3

    $start = Invoke-Cli 'start-recording-all'
    # Some effects do not show in a short window, so a case can widen it.
    $seconds = if ($Case.Seconds) { [int]$Case.Seconds } else { $RecordSeconds }
    Start-Sleep -Seconds $seconds
    $stop = Invoke-Cli 'stop-recording-all'
    Start-Sleep -Seconds 2

    # Continuous recording is cut on key frames, so wait until it has produced the
    # requested number of segments. The segment still open when the workers are killed is
    # EXPECTED to be incomplete (there is no CLI command that shuts the app down cleanly),
    # so the checks below only validate the segments that were closed while the app ran
    # -- which is why the wait targets ContinuousMinSegments and the verdict then asks for
    # ContinuousMinSegments - 1 closed ones.
    $contWaitedOut = $false
    if ($isContinuous) {
        $deadline = (Get-Date).AddSeconds($ContinuousWaitSeconds)
        while ($true) {
            if (@(Get-ChildItem $recDir -Filter 'R1_c*.mp4' -ErrorAction SilentlyContinue).Count -ge $ContinuousMinSegments) { break }
            if ((Get-Date) -ge $deadline) { $contWaitedOut = $true; break }
            Start-Sleep -Seconds 1
        }
    }

    $status = Invoke-Cli 'status'

    Stop-AllWorkers   # flush the log writers

    $initOk    = @(Get-ActivityLines 'recorder\.init ok')
    $initFail  = @(Get-ActivityLines 'recorder\.init fail')
    # The continuous branch picks its own encoder (its properties are left empty so the
    # catalog resolves it). Reporting it matters for the same reason the event encoder is
    # reported: on a real machine this is where you find out WHICH hardware encoder ran,
    # and whether two of them ran at once.
    $contSelected = (@(Get-ActivityLines 'recorder\.continuous-init ok') |
                     ForEach-Object { if ($_ -match "encoder='([^']*)'") { $matches[1] } } | Select-Object -Last 1)
    $selected  = (@(Get-ActivityLines 'gst\.encoder selected') |
                  ForEach-Object { if ($_ -match "encoder='([^']*)'") { $matches[1] } } | Select-Object -Last 1)

    # The N1 signature, now reported by the app itself instead of having to be read out of
    # a .dot file: the pipeline linked and changed state but never reached PLAYING.
    $stalled = @(Get-ActivityLines 'never reached PLAYING')

    # The event recording and the continuous segments live in the same folder and are told
    # apart by name (the continuous template ends in _c<segment>.mp4).
    $contFiles = @(Get-ChildItem $recDir -Filter 'R1_c*.mp4' -ErrorAction SilentlyContinue |
                   Sort-Object CreationTimeUtc)
    # LIMIT TO R1 -- with a second recorder present, R2_*.mp4 would be picked up and the fps misread.
    $mp4   = Get-ChildItem $recDir -Filter 'R1_*.mp4' -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -notlike 'R1_c*' } | Select-Object -First 1
    $probe = if ($mp4) { Test-Mp4 $mp4.FullName } else { $null }

    # Only the segments that were CLOSED count: the last one was open when the workers
    # were killed, so it has no moov and is expected to be unusable.
    $contClosed = @($contFiles | Select-Object -First ([Math]::Max(0, $contFiles.Count - 1)))
    $contBad    = @($contClosed | Where-Object {
        $p = Test-Mp4 $_.FullName
        -not ($p.HasFtyp -and $p.HasMoov -and $p.HasMdat -and $p.HasAvc1 -and (Test-Mp4Duration $p))
    })
    $contInitOk   = @(Get-ActivityLines 'recorder\.continuous-init ok')
    $contInitFail = @(Get-ActivityLines 'recorder\.continuous-init fail')
    $contErrors   = @(Get-ActivityLines 'continuous\.(error|leak|overshoot)')

    if ($Case.ExpectStall) {
        # Inverted expectation: this case is OK precisely when the stall IS reported.
        # Without such a case a broken detector would show up as a clean green run.
        $ok = ($stalled.Count -gt 0) -and ($initFail.Count -eq 1) -and ($initOk.Count -eq 0)
    } else {
        $ok = ($start.ExitCode -eq 0) -and ($stop.ExitCode -eq 0) -and
              ($initOk.Count -eq $expectedRecorders) -and ($initFail.Count -eq 0) -and ($stalled.Count -eq 0) -and
              ($null -ne $probe) -and $probe.HasFtyp -and $probe.HasMoov -and $probe.HasMdat -and
              $probe.HasAvc1 -and (Test-Mp4Duration $probe)

        # The continuous half is judged separately so a failure says which one broke:
        # the event recording above, or the always-on branch here.
        if ($isContinuous) {
            # Ask for one fewer CLOSED segment than requested: the last one is still open
            # when the workers are killed. With the default (2) this is the original
            # "at least one closed segment"; with -ContinuousMinSegments 20 it makes a soak
            # that stopped short fail instead of passing quietly.
            $ok = $ok -and ($contInitOk.Count -eq $expectedRecorders) -and ($contInitFail.Count -eq 0) -and
                  ($contClosed.Count -ge ($ContinuousMinSegments - 1)) -and
                  ($contBad.Count -eq 0) -and ($contErrors.Count -eq 0) -and (-not $contWaitedOut)
        }
    }

    return [pscustomobject]@{
        Case        = $Case.Name
        Note        = $Case.Note
        StartExit   = $start.ExitCode
        StopExit    = $stop.ExitCode
        Selected    = $selected
        InitOk      = $initOk.Count
        InitFail    = $initFail.Count
        Stalled     = $stalled.Count
        Mp4Bytes    = if ($mp4) { $mp4.Length } else { 0 }
        DurationSec = if ($probe) { $probe.DurationSec } else { $null }
        DurationText = Format-Mp4Duration $probe
        FrameCount  = if ($probe) { $probe.FrameCount } else { $null }
        EffectiveFps = if ($probe) { $probe.EffectiveFps } else { $null }
        ValidMp4    = if ($probe) { $probe.HasFtyp -and $probe.HasMoov -and $probe.HasMdat -and $probe.HasAvc1 } else { $false }
        Ok          = $ok
        Continuous  = $isContinuous
        ContSelected   = $contSelected
        ContWaitedOut  = $contWaitedOut
        ContSegments   = $contFiles.Count
        ContClosed     = $contClosed.Count
        ContBad        = $contBad.Count
        ContInitOk     = $contInitOk.Count
        ContInitFail   = $contInitFail.Count
        ContErrorText  = ($contErrors -join "`n")
        Status      = $status.StdOut
        InitFailText = ($initFail -join "`n")
        StalledText  = ($stalled -join "`n")
        StartStdErr  = $start.StdErr
    }
}

if ($CaseFilter) {
    $before = $cases.Count
    $kept = @($cases | Where-Object { $_.Name -match $CaseFilter })
    $cases = New-Object System.Collections.Generic.List[object]
    $kept | ForEach-Object { $cases.Add($_) }
    # ALWAYS PRINT THE SELECTED COUNT. Selecting zero rows and still finishing green is the worst outcome.
    Write-Host "CaseFilter '$CaseFilter' selected $($cases.Count) of $before case(s)."
    if ($cases.Count -eq 0) { Write-Host 'Nothing to run.'; exit 1 }
    Write-Host ''
}

foreach ($case in $cases) {
    Write-Host "== $($case.Name)"
    $r = Invoke-Case -Case $case

    Write-Host ("   exit start/stop = {0}/{1}   selected = {2}   duration = {3}   -> {4}" -f `
        $r.StartExit, $r.StopExit, $r.Selected, $r.DurationText,
        $(if ($r.Ok) { 'OK' } else { 'FAILED' }))

    if ($r.Stalled -gt 0 -and $case.ExpectStall) {
        Write-Host '   (expected) the stall was detected and reported, as it should be' -ForegroundColor DarkGray
    } elseif ($r.Stalled -gt 0) {
        Write-Host '   *** THE N1 SIGNATURE IS STILL PRESENT: the pipeline never reached PLAYING ***' -ForegroundColor Red
        Write-Host "   $($r.StalledText)"
    } elseif (-not $r.Ok) {
        if ($r.InitFailText) { Write-Host "   init fail: $($r.InitFailText)" -ForegroundColor Yellow }
        if ($r.StartStdErr)  { Write-Host "   stderr: $($r.StartStdErr)" }
    }

    $results.Add($r)
    Write-Host ''
}

Stop-AllWorkers

# ---------------------------------------------------------------- report

$reportPath = Join-Path $WorkDir 'high-resolution-report.md'
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('# High-resolution recording verification report (issue N1)')
$null = $sb.AppendLine()
$null = $sb.AppendLine("- Machine: $env:COMPUTERNAME")
$null = $sb.AppendLine("- Publish dir: ``$PublishDir``")
$null = $sb.AppendLine("- Build contains the N1 fix: **$(if ($hasFix) { 'yes' } else { 'NO -- results below are meaningless' })**")
$null = $sb.AppendLine("- Build has continuous recording: **$(if ($hasContinuous) { 'yes' } else { 'NO -- the continuous rows are meaningless' })**")
$null = $sb.AppendLine("- Screen-capture monitor index: $MonitorIndex")
$null = $sb.AppendLine("- Recording window per case: ${RecordSeconds}s")
$null = $sb.AppendLine()
$null = $sb.AppendLine('A case counts as OK only if all of these hold: start and stop both exit 0, exactly one')
$null = $sb.AppendLine('`recorder.init ok` and no `recorder.init fail`, no "never reached PLAYING" anywhere, and a')
$null = $sb.AppendLine('structurally valid MP4 whose duration is not zero. The duration comes from the moov, or,')
$null = $sb.AppendLine('when that is zero (a fragmented MP4 -- the product default), from the sidecar')
$null = $sb.AppendLine('`<file>.mp4.json`. With neither, a file carrying `moof` boxes reads as')
$null = $sb.AppendLine('`fragmented (n moof)` and its length is not judged.')
$null = $sb.AppendLine()
$null = $sb.AppendLine('`event fps` is the frame count divided by that length. The count comes from the moov')
$null = $sb.AppendLine('(`stsz` sample_count) and, for a fragmented file whose moov counts no samples, from the')
$null = $sb.AppendLine('fragments themselves (`moof` -> `traf` -> `trun` sample_count, summed). It reads `n/a`')
$null = $sb.AppendLine('only when there is no length to divide by -- a fragmented file with no sidecar.')
$null = $sb.AppendLine()
$null = $sb.AppendLine("Continuous rows add: exactly one ``recorder.continuous-init ok`` and no ``... fail``, at least")
$null = $sb.AppendLine("$($ContinuousMinSegments - 1) CLOSED segment(s) out of the $ContinuousMinSegments asked for, every closed segment structurally")
$null = $sb.AppendLine('valid, and no `continuous.error` / `continuous.leak` / `continuous.overshoot`. The segment')
$null = $sb.AppendLine('still open when the workers are killed is expected to be incomplete and is not counted')
$null = $sb.AppendLine('(there is no CLI command that shuts the app down cleanly).')
$null = $sb.AppendLine()
$null = $sb.AppendLine('| Case | start/stop | selected encoder | init ok/fail | stalled | MP4 | duration | event fps | segments (closed/bad) | result |')
$null = $sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in $results) {
    $seg = if ($r.Continuous) { '{0} ({1}/{2})' -f $r.ContSegments, $r.ContClosed, $r.ContBad } else { '-' }
    $fps = if ($null -ne $r.EffectiveFps) { '{0} ({1}f)' -f $r.EffectiveFps, $r.FrameCount } else { 'n/a' }
    $null = $sb.AppendLine(('| {0} | {1}/{2} | `{3}` | {4}/{5} | {6} | {7} | {8} | {9} | {10} | {11} |' -f `
        $r.Case, $r.StartExit, $r.StopExit, $r.Selected, $r.InitOk, $r.InitFail, $r.Stalled,
        $(if ($r.ValidMp4) { 'valid' } else { 'INVALID' }), $r.DurationText, $fps, $seg,
        $(if ($r.Ok) { 'OK' } else { '**FAILED**' })))
}

$null = $sb.AppendLine()
$null = $sb.AppendLine('## Notes per case')
foreach ($r in $results) {
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("### $($r.Case)")
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("- $($r.Note)")
    $null = $sb.AppendLine("- MP4 bytes: $($r.Mp4Bytes)")
    if ($r.Continuous) {
        $null = $sb.AppendLine("- continuous encoder: ``$($r.ContSelected)``")
        $null = $sb.AppendLine("- continuous segments: $($r.ContSegments) written, $($r.ContClosed) closed, $($r.ContBad) unusable (asked for $ContinuousMinSegments)")
        if ($r.ContWaitedOut) {
            $null = $sb.AppendLine("- **the wait timed out before $ContinuousMinSegments segments appeared** (-ContinuousWaitSeconds $ContinuousWaitSeconds)")
        }
    }
    if ($r.Status) {
        $null = $sb.AppendLine('- `status` output (name / initialised / recording / last file /')
    $null = $sb.AppendLine('  continuous / continuous file / last error -- trailing empty columns are not shown):')
        $null = $sb.AppendLine('```')
        $null = $sb.AppendLine($r.Status)
        $null = $sb.AppendLine('```')
    }
    if ($r.InitFailText) {
        $null = $sb.AppendLine('- initialisation failure:')
        $null = $sb.AppendLine('```')
        $null = $sb.AppendLine($r.InitFailText)
        $null = $sb.AppendLine('```')
    }
}

$dots = @(Get-ChildItem $dotDir -Filter *.dot -ErrorAction SilentlyContinue)
$null = $sb.AppendLine()
$null = $sb.AppendLine('## Pipeline graphs')
$null = $sb.AppendLine()
if ($dots.Count -eq 0) {
    $null = $sb.AppendLine('None. The app only dumps a .dot on a bus error or warning, so an empty list means')
    $null = $sb.AppendLine('no recorder posted either during the run -- which is the expected outcome.')
} else {
    $null = $sb.AppendLine("$($dots.Count) file(s) were dumped (the app dumps on bus errors and warnings):")
    $null = $sb.AppendLine()
    foreach ($d in $dots) { $null = $sb.AppendLine("- ``$($d.Name)`` ($($d.Length) bytes)") }
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('They are kept in the work directory; send them back along with this report.')
}

Set-Content -Path $reportPath -Value $sb.ToString() -Encoding utf8

Write-Host '---------------------------------------------------------------'
$results | Format-Table Case, StartExit, StopExit, InitOk, InitFail, Stalled, DurationText, Ok -AutoSize
if ($script:lastCaseName) {
    $safe = ($script:lastCaseName -replace '[^A-Za-z0-9]+', '-').Trim('-')
    if ($safe.Length -gt 60) { $safe = $safe.Substring(0, 60) }
    foreach ($log in 'activity.log', 'debug.log') {
        $src = Join-Path $WorkDir $log
        if (Test-Path $src) { Copy-Item $src (Join-Path $logDir "$safe.$log") -Force }
    }
}
Write-Host "Report written to: $reportPath"

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -gt 0) {
    Write-Host ("FAILED cases: {0}" -f $failed.Count) -ForegroundColor Red
    Write-Host "Work dir kept for diagnosis: $WorkDir"
} elseif (-not $KeepWorkDir) {
    if ($dots.Count -eq 0) {
        Copy-Item $reportPath (Join-Path ([System.IO.Path]::GetTempPath()) 'high-resolution-report.md') -Force
        Remove-Item -Recurse -Force $WorkDir
        Write-Host "Work dir removed (report copied to %TEMP%\high-resolution-report.md). Use -KeepWorkDir to retain artefacts."
    } else {
        Write-Host "Work dir kept because .dot files were dumped: $WorkDir"
    }
}

$continuousRequested = @($results | Where-Object { $_.Continuous }).Count -gt 0
exit $(if ($failed.Count -gt 0 -or -not $hasFix -or ($continuousRequested -and -not $hasContinuous)) { 1 } else { 0 })
