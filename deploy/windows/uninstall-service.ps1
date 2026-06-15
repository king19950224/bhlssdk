param(
    [string]$ServiceName = "BoltPreload.Api",
    [string]$NssmPath = "C:\BoltPreload\tools\nssm.exe"
)

$ErrorActionPreference = "Stop"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    if (Test-Path $NssmPath) {
        & $NssmPath stop $ServiceName | Out-Null
        & $NssmPath remove $ServiceName confirm | Out-Null
    } else {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
    }
    Write-Host "Removed service $ServiceName"
} else {
    Write-Host "Service not found: $ServiceName"
}
