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
$gstDll = Join-Path $PublishDir 'GStreamer.dll'
$hasFix = $false
if (Test-Path $gstDll) {
    $bytes = [System.IO.File]::ReadAllBytes($gstDll)
    # The literal lives in the assembly's user string heap as UTF-16. Decode the whole
    # file and use IndexOf rather than a byte loop -- a hand-rolled scan in PowerShell 5.1
    # is slow enough to notice.
    #
    # BOTH byte alignments must be tried. The string does not start at an even file
    # offset, so decoding from byte 0 splits every UTF-16 code unit across the wrong pair
    # and finds nothing. Measured: the marker sits at odd alignment in the current build,
    # so an offset-0-only check reports "old build" for a correctly fixed one -- which is
    # exactly the false negative that would send someone chasing a phantom.
    foreach ($enc in @([System.Text.Encoding]::Unicode, [System.Text.Encoding]::UTF8)) {
        foreach ($offset in 0, 1) {
            if ($offset -ge $bytes.Length) { continue }
            $text = $enc.GetString($bytes, $offset, $bytes.Length - $offset)
            if ($text.IndexOf($fixMarker, [System.StringComparison]::Ordinal) -ge 0) {
                $hasFix = $true
                break
            }
        }
        if ($hasFix) { break }
    }
}

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
$null = New-Item -ItemType Directory -Force $recDir
$null = New-Item -ItemType Directory -Force $dotDir

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
        [int]$BufferDuration = 3000
    )

    $encProp   = if ([string]::IsNullOrEmpty($EncodingProperties)) { 'null' } else { '"' + $EncodingProperties + '"' }
    $tmpl      = ($recDir -replace '\\', '\\') + '\\{Name}_{Now:HHmmssfff}.mp4'
    $logPath   = ($WorkDir -replace '\\', '\\') + '\\debug.log'
    $dotPath   = ($dotDir  -replace '\\', '\\')

    $json = @"
{
  "DataVersion": 1,
  "DebugLogFile": "$logPath",
  "GstDebugDumpDotDir": "$dotPath",
  "GstDebug": "",
  "PreferredH264Encoder": "",
  "Recorders": [
    {
      "Name": "R1",
      "BufferDuration": $BufferDuration,
      "FilenameTemplate": "$tmpl",
      "Type": "$RecorderType",
      "SrcPipeline": "$SrcPipeline",
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
                }
            }
            $fs.Position = $start + $size
        }
        return $res
    } finally { $fs.Dispose() }
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

# This is the encoder line exactly as it appeared in the field report, and it is used for
# the sweep too so that only the resolution differs between rows.
#
# It happens to equal EncoderCatalog's qsv launch string today, and that is not a
# coincidence worth losing: if the catalog changes and this does not, the sweep quietly
# starts verifying a configuration the product no longer produces, and still reports green.
# EncoderCatalogScriptSyncTests pins the two together, so a deliberate catalog change fails
# that test and forces a decision here rather than drifting silently.
$reportedEnc = 'qsvh264enc rate-control=icq icq-quality=30 gop-size=15'

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
}
else {

$cases.Add([pscustomobject]@{
    Name = "REPORTED: 4K screen capture, monitor-index=$MonitorIndex, qsvh264enc icq"
    Type = 'D3d12'; Src = $reportedSrc; Enc = $reportedEnc; Buffer = 10000
    Note = 'the exact configuration from the report (settings, encoder line and BufferDuration all as received)'
})

# Resolution sweep. Everything except width/height is identical, so a difference between
# rows can only be the resolution. 1920x1080 and below used to work; 2560x1440 and above
# used to deadlock (measured on a GPU-less dev machine with the same pipeline shape).
foreach ($wh in @('320x240', '1920x1080', '2560x1440', '3840x2160')) {
    $parts = $wh.Split('x')
    $cases.Add([pscustomobject]@{
        Name = "sweep: d3d12testsrc $wh, qsvh264enc icq"
        Type = 'D3d12'
        Src  = "d3d12testsrc is-live=true do-timestamp=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=$($parts[0]), height=$($parts[1]), framerate=15/1"
        Enc  = $reportedEnc; Buffer = 3000
        Note = if ([int]$parts[0] * [int]$parts[1] * 3 / 2 -gt 5242880) { 'ABOVE the old threshold -- this used to deadlock' } else { 'below the old threshold -- this used to work; must still work' }
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

}   # end of the non-smoke case list

# ---------------------------------------------------------------- run

Write-Host "Publish dir : $PublishDir"
Write-Host "Work dir    : $WorkDir"
Write-Host ("Fixed build : " + $(if ($hasFix) { 'YES (found the leaky preview queue in GStreamer.dll)' } else { 'NO -- THIS LOOKS LIKE AN OLD BUILD' }))
if (-not $hasFix) {
    Write-Warning "GStreamer.dll does not contain the fix marker '$fixMarker'."
    Write-Warning "You are probably running binaries from before the N1 fix. Copy the new publish output and re-run."
}
Write-Host ''

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Case {
    param([object]$Case)

    Stop-AllWorkers
    Get-ChildItem $recDir -Filter *.mp4 -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path $WorkDir 'activity.log') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $WorkDir 'debug.log')    -Force -ErrorAction SilentlyContinue

    Write-Settings -RecorderType $Case.Type -SrcPipeline $Case.Src `
                   -EncodingProperties $Case.Enc -BufferDuration $Case.Buffer

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
    Start-Sleep -Seconds $RecordSeconds
    $stop = Invoke-Cli 'stop-recording-all'
    Start-Sleep -Seconds 2

    $status = Invoke-Cli 'status'

    Stop-AllWorkers   # flush the log writers

    $initOk    = @(Get-ActivityLines 'recorder\.init ok')
    $initFail  = @(Get-ActivityLines 'recorder\.init fail')
    $selected  = (@(Get-ActivityLines 'gst\.encoder selected') |
                  ForEach-Object { if ($_ -match "encoder='([^']*)'") { $matches[1] } } | Select-Object -Last 1)

    # The N1 signature, now reported by the app itself instead of having to be read out of
    # a .dot file: the pipeline linked and changed state but never reached PLAYING.
    $stalled = @(Get-ActivityLines 'never reached PLAYING')

    $mp4   = Get-ChildItem $recDir -Filter *.mp4 -ErrorAction SilentlyContinue | Select-Object -First 1
    $probe = if ($mp4) { Test-Mp4 $mp4.FullName } else { $null }

    if ($Case.ExpectStall) {
        # Inverted expectation: this case is OK precisely when the stall IS reported.
        # Without such a case a broken detector would show up as a clean green run.
        $ok = ($stalled.Count -gt 0) -and ($initFail.Count -eq 1) -and ($initOk.Count -eq 0)
    } else {
        $ok = ($start.ExitCode -eq 0) -and ($stop.ExitCode -eq 0) -and
              ($initOk.Count -eq 1) -and ($initFail.Count -eq 0) -and ($stalled.Count -eq 0) -and
              ($null -ne $probe) -and $probe.HasFtyp -and $probe.HasMoov -and $probe.HasMdat -and
              $probe.HasAvc1 -and ($probe.DurationSec -gt 0)
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
        ValidMp4    = if ($probe) { $probe.HasFtyp -and $probe.HasMoov -and $probe.HasMdat -and $probe.HasAvc1 } else { $false }
        Ok          = $ok
        Status      = $status.StdOut
        InitFailText = ($initFail -join "`n")
        StalledText  = ($stalled -join "`n")
        StartStdErr  = $start.StdErr
    }
}

foreach ($case in $cases) {
    Write-Host "== $($case.Name)"
    $r = Invoke-Case -Case $case

    Write-Host ("   exit start/stop = {0}/{1}   selected = {2}   duration = {3}s   -> {4}" -f `
        $r.StartExit, $r.StopExit, $r.Selected,
        $(if ($null -ne $r.DurationSec) { $r.DurationSec } else { 'n/a' }),
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
$null = $sb.AppendLine("- Screen-capture monitor index: $MonitorIndex")
$null = $sb.AppendLine("- Recording window per case: ${RecordSeconds}s")
$null = $sb.AppendLine()
$null = $sb.AppendLine('A case counts as OK only if all of these hold: start and stop both exit 0, exactly one')
$null = $sb.AppendLine('`recorder.init ok` and no `recorder.init fail`, no "never reached PLAYING" anywhere, and a')
$null = $sb.AppendLine('structurally valid MP4 with a non-zero duration.')
$null = $sb.AppendLine()
$null = $sb.AppendLine('| Case | start/stop | selected encoder | init ok/fail | stalled | MP4 | duration | result |')
$null = $sb.AppendLine('|---|---|---|---|---|---|---|---|')
foreach ($r in $results) {
    $null = $sb.AppendLine(('| {0} | {1}/{2} | `{3}` | {4}/{5} | {6} | {7} | {8}s | {9} |' -f `
        $r.Case, $r.StartExit, $r.StopExit, $r.Selected, $r.InitOk, $r.InitFail, $r.Stalled,
        $(if ($r.ValidMp4) { 'valid' } else { 'INVALID' }), $r.DurationSec,
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
    if ($r.Status) {
        $null = $sb.AppendLine('- `status` output (name / initialised / recording / last file / last error):')
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
$results | Format-Table Case, StartExit, StopExit, InitOk, InitFail, Stalled, DurationSec, Ok -AutoSize
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

exit $(if ($failed.Count -gt 0 -or -not $hasFix) { 1 } else { 0 })
