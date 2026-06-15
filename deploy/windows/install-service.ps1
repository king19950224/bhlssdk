param(
    [string]$ServiceName = "BoltPreload.Api",
    [string]$DisplayName = "Bolt Preload Monitoring Service",
    [string]$InstallDir = "C:\BoltPreload",
    [string]$ExeName = "BoltPreload.Api.exe",
    [string]$NssmPath = "C:\BoltPreload\tools\nssm.exe"
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $InstallDir $ExeName
if (!(Test-Path $exePath)) {
    throw "Executable not found: $exePath"
}

if (!(Test-Path $NssmPath)) {
    throw "NSSM not found: $NssmPath. Put nssm.exe in C:\BoltPreload\tools or pass -NssmPath."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service exists, stopping and removing: $ServiceName"
    & $NssmPath stop $ServiceName | Out-Null
    & $NssmPath remove $ServiceName confirm | Out-Null
    Start-Sleep -Seconds 2
}

& $NssmPath install $ServiceName $exePath | Out-Null
& $NssmPath set $ServiceName DisplayName $DisplayName | Out-Null
& $NssmPath set $ServiceName Description "Industrial bolt preload online monitoring service" | Out-Null
& $NssmPath set $ServiceName AppDirectory $InstallDir | Out-Null
& $NssmPath set $ServiceName Start SERVICE_AUTO_START | Out-Null
& $NssmPath set $ServiceName AppStdout (Join-Path $InstallDir "logs\service-stdout.log") | Out-Null
& $NssmPath set $ServiceName AppStderr (Join-Path $InstallDir "logs\service-stderr.log") | Out-Null
& $NssmPath set $ServiceName AppRotateFiles 1 | Out-Null
& $NssmPath set $ServiceName AppRotateOnline 1 | Out-Null
& $NssmPath set $ServiceName AppRotateSeconds 86400 | Out-Null
& $NssmPath set $ServiceName AppRestartDelay 10000 | Out-Null

New-NetFirewallRule `
    -DisplayName "BoltPreload API 5000" `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 5000 `
    -ErrorAction SilentlyContinue | Out-Null

Start-Service -Name $ServiceName
Write-Host "Installed and started $ServiceName at $exePath"
