param(
    [ValidateSet('auto', 'azure', 'v2', 'mock')]
    [string]$Device = 'auto',
    [ValidateRange(1024, 65535)]
    [int]$Port = 8765,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$bridge = Join-Path $PSScriptRoot 'bridge/TangSancai.Kinect.exe'
if (!(Test-Path -LiteralPath $bridge -PathType Leaf)) {
    throw 'Offline bridge missing. Run Build-Offline.ps1 on the build computer, then use the resulting package.'
}

$root = Join-Path $PSScriptRoot 'web'
$url = "http://127.0.0.1:$Port"
$stdout = Join-Path $PSScriptRoot 'bridge.log'
$stderr = Join-Path $PSScriptRoot 'bridge-error.log'

function Get-BridgeHealth {
    try {
        Invoke-RestMethod "$url/health" -TimeoutSec 1
    } catch {
        $null
    }
}

$health = Get-BridgeHealth
if ($health) {
    if ($health.app -ne 'tangsancai' -or $health.mode -ne $Device) {
        throw "Port $Port is already in use. Close the running exhibit to switch modes, or specify another port."
    }
    if (!$NoBrowser) { Start-Process "$url/" }
    return
}

$process = Start-Process -FilePath $bridge -ArgumentList "--root `"$root`" --device $Device --port $Port" `
    -WorkingDirectory (Split-Path $bridge) -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($process.HasExited) { throw "Bridge exited. See $stderr" }
        $health = Get-BridgeHealth
        if ($health.app -eq 'tangsancai') { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($health.app -ne 'tangsancai') { throw "Bridge did not start. See $stderr" }

    if (!$NoBrowser) { Start-Process "$url/" }
    Write-Host "TangSancai: $url/  Mode: $Device"
    Write-Host 'Close this window or press Ctrl+C to stop the bridge.'
    Wait-Process -Id $process.Id
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
