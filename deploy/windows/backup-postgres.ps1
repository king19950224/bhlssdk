# 非交互跑必须先设 PGPASSWORD 或配好 %APPDATA%\postgresql\pgpass.conf，否则卡在密码提示。
# -F c 出自定义格式：恢复走 pg_restore，可以只挑部分表，比纯 SQL dump 灵活。
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
