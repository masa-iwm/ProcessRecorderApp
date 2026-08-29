<#
.SYNOPSIS
    Unattended verification of recording transcode and the auxiliary encoder slots on a
    machine with a real GPU.

.DESCRIPTION
    Recording transcode re-encodes a finished recording while it is served, and it needs a
    hardware H.264 DECODER. The bundled GStreamer runtime has no software H.264 decoder
    (OpenH264 is deliberately not shipped), so on a machine without one the product answers
    'transcode unavailable' and every case below is inconclusive. That is what the E2E layer
    pins down; this script is the other half -- the true path, which only a GPU box can run.

    Everything is done here: an isolated data directory, a generated settings.json, the
    resident worker, one recording, then HTTP requests against the running server. No manual
    steps and no GUI interaction.

    Output is English on purpose -- Windows PowerShell 5.1 reads .ps1 as ANSI unless the file
    has a BOM, and non-ASCII literals in a script that gets copied between machines are a
    reliable way to break it.

.PARAMETER PublishDir
    The published application directory (output of
    'dotnet publish -p:PublishProfile=win-x64-aot' -- the shipped form is Native AOT).
    Defaults to the repo's AOT publish output relative to this script.

.PARAMETER WorkDir
    Where to put the isolated data directory and the report.
    Defaults to a new folder under %TEMP%.

.PARAMETER RecordSeconds
    How long to record the clip that gets transcoded.

.PARAMETER Port
    The port the remote control server listens on (127.0.0.1 only).

.PARAMETER Limit
    RemoteAuxiliaryEncoderLimit -- how many auxiliary encoders may run at once.

.PARAMETER KeepWorkDir
    Keep the working directory (settings.json, debug.log, MP4s) after the run.

.EXAMPLE
    .\Verify-Transcode.ps1
    .\Verify-Transcode.ps1 -PublishDir D:\pra\publish -Limit 3 -KeepWorkDir

.OUTPUTS
    A markdown report at <WorkDir>\transcode-report.md and the same summary on stdout.
    Exit code 0 only if every case actually ran AND behaved as expected; 1 otherwise
    (a skipped case verified nothing, so it counts as inconclusive, not as success).
    On a machine with no hardware H.264 decoder the 'capabilities' case FAILS and the
    transcode cases are SKIPPED -- that is the expected outcome there, not a defect.
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$WorkDir,
    [int]$RecordSeconds = 6,
    [int]$Port = 8760,
    [int]$Limit = 2,
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'

# How long the server keeps an auxiliary encoder slot after the reader closed
# (Components.TranscodeLimits.GraceMs). A slot is not free the instant a response ends:
# the next request of the same session is meant to inherit it.
$script:GraceSeconds = 10

# NOTE: never use Start-Process -Wait here. It waits for the whole process tree, and the
# resident worker never exits, so it would hang forever.

# .NET keeps two connections per endpoint by default. The busy case holds several responses
# open at once, so without this the extra requests queue on the client and the case would
# hang instead of seeing the server's 409.
[System.Net.ServicePointManager]::DefaultConnectionLimit = 32

# `break` rethrows, so this only adds the cleanup; it does not swallow the failure. Stopping
# this run's workers matters: an abnormal exit would otherwise leave a resident worker
# running against the (now abandoned) temp profile until the machine reboots.
trap {
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

if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('pra-transcode-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)
$null = New-Item -ItemType Directory -Force $WorkDir

# Isolate completely from any resident instance the developer may have running on this
# machine: without BOTH variables the run would forward its commands to that instance (and
# its recordings would land in the real user profile).
$env:PROCESSRECORDERAPP_DATA_DIR   = $WorkDir
$env:PROCESSRECORDERAPP_KEY_PREFIX = 'PraTranscodeVerify_' + [guid]::NewGuid().ToString('N')

$recDir = Join-Path $WorkDir 'rec'
$null = New-Item -ItemType Directory -Force $recDir

# The token is generated per run and only ever written into the isolated settings.json.
$tokenBytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($tokenBytes)
$token = [Convert]::ToBase64String($tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

Write-Host '---------------------------------------------------------------'
Write-Host "Publish dir : $PublishDir"
Write-Host "Work dir    : $WorkDir"
Write-Host "Port        : $Port"
Write-Host "Slot limit  : $Limit"
Write-Host '---------------------------------------------------------------'

# ---------------------------------------------------------------- helpers

# Kill only the workers that belong to THIS run. They are identified by the pid each worker
# writes to the isolated activity.log ('app.start pid=N') under $WorkDir -- the same
# discipline the C# harness uses. Matching by process name would also kill a real resident
# instance the user keeps running, which is exactly what the isolation above promises not to
# do (an in-progress recording of that instance would be lost unfinalized).
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

function Invoke-Cli {
    param([string]$Arguments, [int]$TimeoutMs = 90000)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $exe
    $psi.Arguments              = $Arguments
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    $p = [System.Diagnostics.Process]::Start($psi)

    # Read both pipes asynchronously BEFORE waiting. A synchronous ReadToEnd never returns
    # while a hung CLI keeps stdout open, and deadlocks outright once the process fills the
    # other pipe's buffer.
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
    # 640x480@15: the transcode presets are relative to the source, so the frame has to be
    # tall enough for more than one of them to be offered (480p and 360p here).
    $src = 'videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420,width=640,height=480,framerate=15/1'
    $tmpl    = ($recDir -replace '\\', '\\') + '\\{Name}_{Now:HHmmssfff}.mp4'
    $logPath = ($WorkDir -replace '\\', '\\') + '\\debug.log'
    $outDir  = ($recDir -replace '\\', '\\')

    # FragmentedOutput is left at the product default (true). The converter reads the file
    # with filesrc either way, and the seek case is the one that cares about the shape --
    # a fragmented input starts from the frame that carries the position, a non-fragmented
    # one from the key frame before it.
    $json = @"
{
  "DataVersion": 1,
  "DebugLogFile": "$logPath",
  "OutputDirectory": "$outDir",
  "RemoteControlEnabled": true,
  "RemoteControlBindAddress": "127.0.0.1",
  "RemoteControlPort": $Port,
  "RemoteControlAccessToken": "$token",
  "RemoteControlAllowGuestRead": false,
  "RemoteAuxiliaryEncoderLimit": $Limit,
  "Recorders": [
    {
      "Name": "R1",
      "BufferDuration": 2000,
      "FilenameTemplate": "$tmpl",
      "Type": "System",
      "SrcPipeline": "$src",
      "EncodingProperties": null
    }
  ]
}
"@
    Set-Content -Path (Join-Path $WorkDir 'settings.json') -Value $json -Encoding utf8
}

# ---------------------------------------------------------------- HTTP

# HttpWebRequest, not Invoke-WebRequest: the streaming cases have to hold a response open
# and read it themselves, and 4xx/5xx have to be read as answers rather than as terminating
# errors (Invoke-WebRequest throws and hides the body).
function New-ApiRequest {
    param([string]$Path, [int]$TimeoutMs = 30000)

    $request = [System.Net.HttpWebRequest]::Create("http://127.0.0.1:$Port/$Path")
    $request.Method           = 'GET'
    $request.Timeout          = $TimeoutMs
    $request.ReadWriteTimeout = $TimeoutMs
    $request.Headers.Add('Authorization', "Bearer $token")
    return $request
}

function Get-HeaderMap {
    param($Response)

    $map = @{}
    foreach ($name in $Response.Headers.AllKeys) { $map[$name] = $Response.Headers[$name] }
    return $map
}

# A whole answer as text. Refusals come back the same way as successes -- with their status
# and body -- because the body carries the reason ('auxiliary encoder busy' and the like).
function Invoke-Api {
    param([string]$Path, [int]$TimeoutMs = 30000)

    $request = New-ApiRequest -Path $Path -TimeoutMs $TimeoutMs
    try {
        $response = $request.GetResponse()
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $result = [pscustomobject]@{
            Status  = [int]$response.StatusCode
            Body    = $body
            Headers = (Get-HeaderMap $response)
            Error   = $null
        }
        $reader.Close()
        $response.Close()
        return $result
    } catch [System.Net.WebException] {
        $webResponse = $_.Exception.Response
        if ($null -eq $webResponse) {
            return [pscustomobject]@{ Status = 0; Body = ''; Headers = @{}; Error = $_.Exception.Message }
        }
        $reader = New-Object System.IO.StreamReader($webResponse.GetResponseStream(), [System.Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $result = [pscustomobject]@{
            Status  = [int]$webResponse.StatusCode
            Body    = $body
            Headers = (Get-HeaderMap $webResponse)
            Error   = $null
        }
        $reader.Close()
        $webResponse.Close()
        return $result
    }
}

function Get-TranscodePath {
    param([string]$Recording, [double]$Start, [string]$Quality, [string]$Session)

    $startText = $Start.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
    return "api/recording-transcode/$Recording" + "?start=$startText&q=$Quality&session=$Session"
}

# Open a transcode and stop at the headers. **GetResponse() returning means the server has
# already produced ftyp+moov** -- it buffers the init before writing the response headers,
# because X-Codecs can only be read out of it -- so the auxiliary encoder slot is held from
# this point until the returned response is closed.
function Open-Transcode {
    param([string]$Recording, [double]$Start, [string]$Quality, [string]$Session, [int]$TimeoutMs = 60000)

    $request = New-ApiRequest -Path (Get-TranscodePath $Recording $Start $Quality $Session) -TimeoutMs $TimeoutMs
    try {
        $response = $request.GetResponse()
        return [pscustomobject]@{
            Status   = [int]$response.StatusCode
            Headers  = (Get-HeaderMap $response)
            Response = $response
            Request  = $request
            Body     = ''
        }
    } catch [System.Net.WebException] {
        $webResponse = $_.Exception.Response
        if ($null -eq $webResponse) {
            return [pscustomobject]@{ Status = 0; Headers = @{}; Response = $null; Request = $null; Body = $_.Exception.Message }
        }
        $reader = New-Object System.IO.StreamReader($webResponse.GetResponseStream(), [System.Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $result = [pscustomobject]@{
            Status   = [int]$webResponse.StatusCode
            Headers  = (Get-HeaderMap $webResponse)
            Response = $null
            Request  = $null
            Body     = $body
        }
        $reader.Close()
        $webResponse.Close()
        return $result
    }
}

# Give the slot back now. **Response.Close() is not enough**: it drains what is left of the
# chunked body first, and what is left of a transcode is the rest of the recording coming
# through an encoder -- the slot would be held for as long as that takes. Abort() on the
# request cuts the socket, which is what the server reads as the client having gone.
function Close-Transcode {
    param($Opened)

    if ($null -eq $Opened) { return }
    if ($null -ne $Opened.Request) {
        try { $Opened.Request.Abort() } catch { }
    }
    if ($null -ne $Opened.Response) {
        try { $Opened.Response.Close() } catch { }
    }
}

# Read an opened transcode to its end and return the bytes.
function Read-ToEnd {
    param($Opened)

    $memory = New-Object System.IO.MemoryStream
    $stream = $Opened.Response.GetResponseStream()
    $buffer = New-Object byte[] 65536
    while ($true) {
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { break }
        $memory.Write($buffer, 0, $read)
    }
    $bytes = $memory.ToArray()
    $memory.Close()
    $Opened.Response.Close()
    return ,$bytes
}

# Is a four-character ISO-BMFF box type present anywhere in the bytes? Deliberately a plain
# scan rather than a box walk: the question here is only "does the stream carry an init
# segment and at least one fragment", and the product's own tests parse the boxes properly.
function Test-BoxPresent {
    param([byte[]]$Bytes, [string]$Type)

    $needle = [System.Text.Encoding]::ASCII.GetBytes($Type)
    $limit = $Bytes.Length - $needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        $hit = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($Bytes[$i + $j] -ne $needle[$j]) { $hit = $false; break }
        }
        if ($hit) { return $true }
    }
    return $false
}

# The DASH manifest, polled until it is served. The live preview holds one auxiliary encoder
# slot while its mux exists, and the mux is built on the first sample after the manifest was
# asked for -- so 503 'dash preview is starting' is the normal first answer.
function Wait-DashManifest {
    param([int]$TimeoutSeconds = 30)

    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $last = '(no response)'
    while ($deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $answer = Invoke-Api 'api/recorders/R1/dash/manifest.mpd'
        if ($answer.Status -eq 200) { return $true }
        $last = "$($answer.Status) $($answer.Body)"
        Start-Sleep -Milliseconds 1000
    }
    Write-Host "   DASH manifest never became 200: $last"
    return $false
}

# ---------------------------------------------------------------- cases

$results = New-Object System.Collections.ArrayList

function Add-Result {
    param([string]$Case, [string]$Result, [string]$Detail)

    $null = $results.Add([pscustomobject]@{ Case = $Case; Result = $Result; Detail = $Detail })
    Write-Host ("   {0,-22} {1,-8} {2}" -f $Case, $Result, $Detail)
}

Write-Settings

Write-Host 'Starting the worker...'
$ping = Invoke-Cli 'ping'
if ($ping.ExitCode -ne 0) {
    # The very first launch on a machine builds the GStreamer plugin registry, which can take
    # longer than the launcher's wait. Retry before giving up.
    Write-Host '   (first launch timed out; retrying)'
    Start-Sleep -Seconds 5
    $ping = Invoke-Cli 'ping'
}
if ($ping.ExitCode -ne 0) {
    Stop-AllWorkers
    throw "The worker did not start (ping exit $($ping.ExitCode)). $($ping.StdErr)"
}

# Let the ring buffer fill before recording (BufferDuration=2000).
Start-Sleep -Seconds 4

Write-Host "Recording ${RecordSeconds}s..."
$startCli = Invoke-Cli 'start-recording-all'
Start-Sleep -Seconds $RecordSeconds
$stopCli  = Invoke-Cli 'stop-recording-all'
Start-Sleep -Seconds 2

if ($startCli.ExitCode -ne 0 -or $stopCli.ExitCode -ne 0) {
    Stop-AllWorkers
    throw "Recording failed (start exit $($startCli.ExitCode), stop exit $($stopCli.ExitCode))."
}

Write-Host 'Cases:'

# ---- capabilities
$decoder = '(not reported)'
$transcodeAvailable = $false
$answer = Invoke-Api 'api/capabilities'
if ($answer.Status -ne 200) {
    Add-Result 'capabilities' 'FAILED' "HTTP $($answer.Status) $($answer.Body)"
} else {
    $capabilities = $answer.Body | ConvertFrom-Json
    $transcodeAvailable = [bool]$capabilities.transcode
    if ($null -ne $capabilities.decoder) { $decoder = $capabilities.decoder }
    $detail = "transcode=$($capabilities.transcode) decoder=$decoder limit=$($capabilities.auxiliaryEncoderLimit)"
    if ($transcodeAvailable) {
        Add-Result 'capabilities' 'OK' $detail
    } else {
        # Expected on a machine with no hardware H.264 decoder. It is still a FAILED row:
        # this script exists to verify the true path, and nothing below it can run.
        Add-Result 'capabilities' 'FAILED' "$detail (no hardware H.264 decoder on this machine)"
    }
}

# ---- list
$recording = $null
$answer = Invoke-Api 'api/recordings'
if ($answer.Status -ne 200) {
    Add-Result 'list' 'FAILED' "HTTP $($answer.Status) $($answer.Body)"
} else {
    $listing = $answer.Body | ConvertFrom-Json
    $finished = @($listing.files | Where-Object { -not $_.inProgress })
    if ($finished.Count -eq 0) {
        Add-Result 'list' 'FAILED' "no finished recording under $($listing.root)"
    } else {
        $recording = $finished[0].path
        Add-Result 'list' 'OK' "$recording ($($finished[0].length) bytes, $($finished[0].width)x$($finished[0].height))"
    }
}

$skipReason = $null
if (-not $transcodeAvailable) { $skipReason = 'no hardware H.264 decoder' }
elseif ($null -eq $recording) { $skipReason = 'no recording to convert' }

# ---- transcode-start0
$start0Bytes = 0
if ($skipReason) {
    Add-Result 'transcode-start0' 'SKIPPED' $skipReason
} else {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $opened = Open-Transcode -Recording $recording -Start 0 -Quality '360p' -Session 'v1'
    if ($opened.Status -ne 200) {
        Add-Result 'transcode-start0' 'FAILED' "HTTP $($opened.Status) $($opened.Body)"
    } else {
        $bytes = Read-ToEnd $opened
        $watch.Stop()
        $start0Bytes = $bytes.Length
        $hasInit = (Test-BoxPresent $bytes 'ftyp') -and (Test-BoxPresent $bytes 'moov')
        $hasFragment = Test-BoxPresent $bytes 'moof'
        $detail = "codecs=$($opened.Headers['X-Codecs']) quality=$($opened.Headers['X-Transcode-Quality'])" `
            + " start=$($opened.Headers['X-Transcode-Start']) bytes=$start0Bytes" `
            + (" {0:F1}s" -f $watch.Elapsed.TotalSeconds)
        if ($hasInit -and $hasFragment) {
            Add-Result 'transcode-start0' 'OK' $detail
        } else {
            Add-Result 'transcode-start0' 'FAILED' "ftyp/moov=$hasInit moof=$hasFragment $detail"
        }
    }
}

# ---- transcode-seek (same session: the slot is handed over, not asked for twice)
if ($skipReason) {
    Add-Result 'transcode-seek' 'SKIPPED' $skipReason
} elseif ($start0Bytes -eq 0) {
    Add-Result 'transcode-seek' 'SKIPPED' 'start0 produced nothing to compare against'
} else {
    $middle = [math]::Round($RecordSeconds / 2.0, 3)
    $opened = Open-Transcode -Recording $recording -Start $middle -Quality '360p' -Session 'v1'
    if ($opened.Status -ne 200) {
        Add-Result 'transcode-seek' 'FAILED' "HTTP $($opened.Status) $($opened.Body)"
    } else {
        $bytes = Read-ToEnd $opened
        $hasFragment = Test-BoxPresent $bytes 'moof'
        $detail = "start=$($opened.Headers['X-Transcode-Start']) bytes=$($bytes.Length) (start0=$start0Bytes)"
        # Shorter than the whole clip: converting from the middle must not deliver the file
        # from its beginning, which is exactly what a seek that was silently ignored does.
        if ($hasFragment -and $bytes.Length -lt $start0Bytes) {
            Add-Result 'transcode-seek' 'OK' $detail
        } else {
            Add-Result 'transcode-seek' 'FAILED' "moof=$hasFragment $detail"
        }
    }
    # Let the handed-over lease expire before the next case counts slots.
    Start-Sleep -Seconds ($script:GraceSeconds + 2)
}

# ---- transcode-busy
if ($skipReason) {
    Add-Result 'transcode-busy' 'SKIPPED' $skipReason
} else {
    $held = @()
    $failure = $null
    for ($i = 1; $i -le $Limit; $i++) {
        $opened = Open-Transcode -Recording $recording -Start 0 -Quality '360p' -Session ("hold$i")
        if ($opened.Status -ne 200) {
            $failure = "holding #$i answered HTTP $($opened.Status) $($opened.Body)"
            break
        }
        $held += $opened
    }

    if ($failure) {
        Add-Result 'transcode-busy' 'FAILED' $failure
    } else {
        $extra = Open-Transcode -Recording $recording -Start 0 -Quality '360p' -Session 'overflow'
        $refused = $extra.Status -eq 409 -and $extra.Body -match 'auxiliary encoder busy'
        Close-Transcode $extra
        foreach ($opened in $held) { Close-Transcode $opened }

        if (-not $refused) {
            Add-Result 'transcode-busy' 'FAILED' "the $($Limit + 1)th answered HTTP $($extra.Status) $($extra.Body)"
        } else {
            # The slot is kept for GraceMs after the reader closed (a seek is meant to
            # inherit it), so the recovery is only observable after that window.
            Start-Sleep -Seconds ($script:GraceSeconds + 2)
            $again = Open-Transcode -Recording $recording -Start 0 -Quality '360p' -Session 'after-busy'
            $recovered = $again.Status -eq 200
            Close-Transcode $again
            if ($recovered) {
                Add-Result 'transcode-busy' 'OK' "$Limit held, the next was 409, and a slot came back after the grace window"
            } else {
                Add-Result 'transcode-busy' 'FAILED' "after the grace window the answer was HTTP $($again.Status) $($again.Body)"
            }
        }
    }
    Start-Sleep -Seconds ($script:GraceSeconds + 2)
}

# ---- dash-shares-the-slots
if ($skipReason) {
    Add-Result 'dash-shares-the-slots' 'SKIPPED' $skipReason
} elseif ($Limit -lt 2) {
    Add-Result 'dash-shares-the-slots' 'SKIPPED' 'needs -Limit 2 or more'
} else {
    if (-not (Wait-DashManifest -TimeoutSeconds 30)) {
        Add-Result 'dash-shares-the-slots' 'FAILED' 'the DASH preview never started'
    } else {
        # The live preview now holds one slot, so only $Limit-1 transcodes can start and the
        # $Limit-th must be refused. **The manifest is polled between the attempts**: a
        # preview nobody reads folds its mux after its lease (10s) and gives the slot back,
        # which would quietly remove the very condition under test.
        $held = @()
        $lastStatus = 0
        $lastBody = ''
        for ($i = 1; $i -le $Limit; $i++) {
            $null = Invoke-Api 'api/recorders/R1/dash/manifest.mpd'
            $opened = Open-Transcode -Recording $recording -Start 0 -Quality '360p' -Session ("dash$i")
            $lastStatus = $opened.Status
            $lastBody = $opened.Body
            if ($opened.Status -eq 200) { $held += $opened } else { break }
        }

        $refused = $lastStatus -eq 409 -and $lastBody -match 'auxiliary encoder busy'
        $detail = "DASH + $($held.Count) transcode(s), then HTTP $lastStatus"
        foreach ($opened in $held) { Close-Transcode $opened }

        if ($refused -and $held.Count -eq ($Limit - 1)) {
            Add-Result 'dash-shares-the-slots' 'OK' $detail
        } else {
            Add-Result 'dash-shares-the-slots' 'FAILED' "$detail $lastBody"
        }
    }
}

Stop-AllWorkers

# ---------------------------------------------------------------- report

$reportPath = Join-Path $WorkDir 'transcode-report.md'
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('# Recording transcode verification report')
$null = $sb.AppendLine()
$null = $sb.AppendLine("- Machine: $env:COMPUTERNAME")
$null = $sb.AppendLine("- Publish dir: ``$PublishDir``")
$null = $sb.AppendLine("- H.264 decoder the app found: ``$decoder``")
$null = $sb.AppendLine("- Auxiliary encoder limit: $Limit")
$null = $sb.AppendLine("- Recording: ``$(if ($recording) { $recording } else { '(none)' })`` (${RecordSeconds}s)")
$null = $sb.AppendLine()
if (-not $transcodeAvailable) {
    $null = $sb.AppendLine('**This machine cannot transcode.** The bundled runtime has no software H.264 decoder, ')
    $null = $sb.AppendLine('so every case below the capability check is SKIPPED and the run is inconclusive by design. ')
    $null = $sb.AppendLine('Run this script on a box with a hardware H.264 decoder (d3d11h264dec / d3d12h264dec / ')
    $null = $sb.AppendLine('nvh264dec / qsvh264dec).')
    $null = $sb.AppendLine()
}
$null = $sb.AppendLine('| Case | Result | Detail |')
$null = $sb.AppendLine('|---|---|---|')
foreach ($r in $results) {
    $null = $sb.AppendLine(('| {0} | {1} | {2} |' -f $r.Case, $r.Result, $r.Detail))
}
$null = $sb.AppendLine()
$null = $sb.AppendLine('## What each case pins down')
$null = $sb.AppendLine()
$null = $sb.AppendLine('- `capabilities` -- the decoder was found and reported. FAILED here means everything else is inconclusive.')
$null = $sb.AppendLine('- `list` -- the recording that was just made is visible over HTTP and is finished (a recording in progress is refused with 409).')
$null = $sb.AppendLine('- `transcode-start0` -- one whole conversion from the beginning: `ftyp`+`moov` once, at least one `moof`, and the codecs header the browser needs.')
$null = $sb.AppendLine('- `transcode-seek` -- the same session asking for the middle of the file gets less than the whole of it (a seek that was ignored would deliver the file again).')
$null = $sb.AppendLine('- `transcode-busy` -- the limit is real: holding every slot makes the next request 409 `auxiliary encoder busy`, and a slot comes back after the grace window.')
$null = $sb.AppendLine('- `dash-shares-the-slots` -- the live preview and the transcodes draw on ONE pool, not two.')
Set-Content -Path $reportPath -Value $sb.ToString() -Encoding utf8

Write-Host '---------------------------------------------------------------'
$results | Format-Table -AutoSize
Write-Host "Report written to: $reportPath"

$failed = @($results | Where-Object { $_.Result -eq 'FAILED' })
$skipped = @($results | Where-Object { $_.Result -eq 'SKIPPED' })
if ($failed.Count -gt 0) { Write-Host ("FAILED cases: {0}" -f $failed.Count) -ForegroundColor Red }
if ($skipped.Count -gt 0) {
    Write-Host ("SKIPPED cases (nothing was verified): {0}" -f $skipped.Count) -ForegroundColor Yellow
}

# A skipped case verified NOTHING, so the run is not green either -- exiting 0 there would be
# the same class of false green as a filter that selects no tests.
$inconclusive = $failed.Count + $skipped.Count

if (-not $KeepWorkDir -and $inconclusive -eq 0) {
    Copy-Item $reportPath (Join-Path ([System.IO.Path]::GetTempPath()) 'transcode-report.md') -Force
    Remove-Item -Recurse -Force $WorkDir
    Write-Host "Work dir removed (report copied to %TEMP%\transcode-report.md). Use -KeepWorkDir to retain artefacts."
}

exit $(if ($inconclusive -gt 0) { 1 } else { 0 })
