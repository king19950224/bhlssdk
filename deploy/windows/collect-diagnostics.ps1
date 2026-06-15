param(
    [string]$InstallDir = "C:\BoltPreload",
    [string]$OutputDir = "C:\BoltPreload\diagnostics"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$bundle = Join-Path $OutputDir "boltpreload-diagnostics-$timestamp"
New-Item -ItemType Directory -Path $bundle | Out-Null

Get-Service | Where-Object { $_.Name -like "*BoltPreload*" } |
    Out-File (Join-Path $bundle "services.txt")

Get-Process | Sort-Object CPU -Descending |
    Select-Object -First 50 |
    Out-File (Join-Path $bundle "processes.txt")

netstat -ano | Out-File (Join-Path $bundle "netstat.txt")

if (Test-Path (Join-Path $InstallDir "logs")) {
    Copy-Item (Join-Path $InstallDir "logs") (Join-Path $bundle "logs") -Recurse -Force
}

if (Test-Path (Join-Path $InstallDir "appsettings.json")) {
    Copy-Item (Join-Path $InstallDir "appsettings.json") (Join-Path $bundle "appsettings.json") -Force
}

Compress-Archive -Path "$bundle\*" -DestinationPath "$bundle.zip" -Force
Write-Host "Diagnostics bundle: $bundle.zip"
