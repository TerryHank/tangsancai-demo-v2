param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'offline')
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $PSScriptRoot 'kinect_bridge/TangSancai.Kinect.csproj'
$bridge = Join-Path $output 'bridge'
$web = Join-Path $output 'web'
New-Item -ItemType Directory -Force $bridge, $web | Out-Null
dotnet publish $project -c Release -o $bridge "-p:OutDir=$bridge/"
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the Kinect bridge.' }

foreach ($path in 'index.html', 'motion_capture.js', 'clothing_color_worker.js', 'assets', 'video', 'final_vid') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $path) -Destination $web -Recurse -Force
}
foreach ($path in 'Start-TangSancai.cmd', 'Start-TangSancai.ps1', 'Start-Simulation.cmd', 'README.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $path) -Destination $output -Force
}
Write-Host "Offline folder: $output"
