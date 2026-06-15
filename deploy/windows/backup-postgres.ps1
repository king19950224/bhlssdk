param(
    [string]$PgDump = "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
    [string]$Database = "boltpreload",
    [string]$User = "boltpreload",
    [string]$HostName = "127.0.0.1",
    [string]$BackupDir = "C:\BoltPreload\backups"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outFile = Join-Path $BackupDir "$Database-$timestamp.dump"

& $PgDump -h $HostName -U $User -d $Database -F c -f $outFile

if (!(Test-Path $outFile)) {
    throw "Backup failed: $outFile not created"
}

Write-Host "Backup written: $outFile"
