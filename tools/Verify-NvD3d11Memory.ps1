<#
.SYNOPSIS
    Settle the open question about nvd3d11h264enc: does the D3d12 recording path really
    have to round-trip every frame through system memory?

.DESCRIPTION
    EncoderCatalog marks nvd3d11h264enc as NeedsSystemMemory:true, which makes
    EventRecorder.BuildSinkPipeline emit

        d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! nvd3d11h264enc

    memory:SystemMemory is an explicit feature, so it cannot match memory:D3D11Memory --
    if that capsfilter is what forces the download, the GPU->CPU->GPU trip is ours, not
    the element's. This script runs the four candidate shapes and prints what each pad
    actually negotiated, so the answer comes from the machine and not from reasoning.

    Needs an NVIDIA machine. Read-only: it records nothing and changes no settings.

    Output is English on purpose (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).

.PARAMETER PublishDir
    The unpacked bundled build (the folder that has gstreamer\win-x64 under it).

.EXAMPLE
    .\Verify-NvD3d11Memory.ps1 -PublishDir D:\...\ProcessRecorderApp-v0.1.0-preview.6-win-x64-gstreamer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDir,
    # Only for dry-running the script where no NVIDIA card is present. The question this
    # script exists to answer is about nvd3d11h264enc; other encoders just exercise the flow.
    [string] $Encoder = 'nvd3d11h264enc')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path (Resolve-Path $PublishDir).Path 'gstreamer\win-x64'
$bin = Join-Path $root 'bin'
$launch = Join-Path $bin 'gst-launch-1.0.exe'
$inspect = Join-Path $bin 'gst-inspect-1.0.exe'
if (-not (Test-Path $launch)) { throw "not found: $launch" }

$savedPath = $env:PATH
$savedReg = $env:GST_REGISTRY
$reg = Join-Path $env:TEMP 'pra-nvmem-registry.bin'

function Invoke-Gst {
    param([string] $Exe, [string] $Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $code = $p.ExitCode
    return [pscustomobject]@{
        ExitCode = $code
        StdOut   = $out.Result
        StdErr   = $err.Result
    }
}

try {
    if ([IO.File]::Exists($reg)) { [IO.File]::Delete($reg) }
    $env:GST_REGISTRY = $reg
    $env:GST_REGISTRY_FORK = 'no'
    $env:GST_PLUGIN_SYSTEM_PATH = (Join-Path $root 'lib\gstreamer-1.0')
    $env:GST_PLUGIN_PATH = ''
    $env:PATH = $bin + ';' + $savedPath

    Write-Host "runtime : $root"
    Write-Host ''

    # ---- 1. what does the element actually accept? --------------------------------
    Write-Host ("== gst-inspect {0}: SINK caps" -f $Encoder)
    $ins = Invoke-Gst -Exe $inspect -Arguments $Encoder
    if ($ins.ExitCode -ne 0) {
        Write-Host '   element not found on this machine -- nothing else can be answered here.'
        return
    }
    $lines = $ins.StdOut -split "`r?`n"
    $sinkAt = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'SINK template') { $sinkAt = $i }
        elseif ($sinkAt -ge 0 -and $lines[$i] -match 'SRC template') { break }
    }
    $sinkCaps = @()
    if ($sinkAt -ge 0) {
        for ($i = $sinkAt; $i -lt $lines.Count; $i++) {
            if ($i -gt $sinkAt -and $lines[$i] -match 'SRC template') { break }
            if ($lines[$i] -match '^\s{6}\S+/\S') { $sinkCaps += $lines[$i].Trim() }
        }
    }
    foreach ($c in $sinkCaps) { Write-Host "   $c" }
    $acceptsD3d12 = @($sinkCaps | Where-Object { $_ -match 'D3D12Memory' }).Count -gt 0
    $acceptsD3d11 = @($sinkCaps | Where-Object { $_ -match 'D3D11Memory' }).Count -gt 0
    Write-Host ("   accepts D3D12Memory = {0} / D3D11Memory = {1}" -f $acceptsD3d12, $acceptsD3d11)
    Write-Host ''

    # ---- 2. the four shapes -------------------------------------------------------
    $head = 'd3d12testsrc num-buffers=30 ! video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720, framerate=15/1 ! d3d12convert ! video/x-raw(memory:D3D12Memory), format=NV12 ! '
    $tail = $Encoder + ' gop-size=15 ! h264parse ! fakesink sync=false'
    $cases = @(
        @{ n = 'A: current shape (download + explicit SystemMemory + videoconvert)';
           p = $head + 'd3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! ' + $tail },
        @{ n = 'B: download, no capsfilter, no videoconvert';
           p = $head + 'd3d12download ! ' + $tail },
        @{ n = 'C: download + videoconvert, no capsfilter';
           p = $head + 'd3d12download ! videoconvert ! ' + $tail },
        @{ n = 'D: no download at all (D3D12 memory straight into the encoder)';
           p = $head + $tail }
    )

    foreach ($c in $cases) {
        Write-Host ('== ' + $c.n)
        $r = Invoke-Gst -Exe $launch -Arguments ('-v -e ' + $c.p)
        $o = $r.StdOut -split "`r?`n"
        $encPad = $Encoder + '0.GstPad:sink: caps'
        $encCaps = @($o | Where-Object { $_ -match [regex]::Escape($encPad) } | Select-Object -Last 1)
        $dlCaps = @($o | Where-Object { $_ -match 'd3d12download0.GstPad:src: caps' } | Select-Object -Last 1)
        # gst-launch reports a link failure as "WARNING: erroneous pipeline", not ERROR --
        # matching only ERROR prints an empty reason for exactly the case that matters most.
        $why = 'ERROR|erroneous pipeline|could not link'
        $err = @($o | Where-Object { $_ -match $why } | Select-Object -First 1)
        if (-not $err) { $err = @($r.StdErr -split "`r?`n" | Where-Object { $_ -match $why } | Select-Object -First 1) }
        Write-Host ("   exit = {0}" -f $r.ExitCode)
        if ($dlCaps) { Write-Host ("   d3d12download src : " + (($dlCaps -join ' ') -replace '.*caps = ', '')) }
        if ($encCaps) { Write-Host ("   encoder sink      : " + (($encCaps -join ' ') -replace '.*caps = ', '')) }
        if ($err) { Write-Host ("   first error       : " + ($err -join ' ')) }
        Write-Host ''
    }

    Write-Host 'How to read this:'
    Write-Host '  D links  -> NeedsSystemMemory should be false; no d3d12download is needed at all.'
    Write-Host '  D fails, B or C links AND the encoder sink caps say memory:D3D11Memory'
    Write-Host '           -> the explicit video/x-raw(memory:SystemMemory) capsfilter is what forces'
    Write-Host '              the GPU->CPU round trip; the download itself can stay on the GPU.'
    Write-Host '  Only A links, or B/C negotiate plain video/x-raw'
    Write-Host '           -> the current shape is required; the round trip is not ours to remove.'
}
finally {
    $env:PATH = $savedPath
    $env:GST_REGISTRY = $savedReg
}
