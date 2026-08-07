# 非交互跑必须能拿到口令：设 PGPASSWORD、配好 pgpass.conf，或直接传 -PgPassword。
# 计划任务跑在 SYSTEM 账户时 pgpass.conf 的路径见 deployment-guide §7.1；-PgPassword 会出现在
# 任务定义里（本机管理员可读），信任边界与 NSSM 服务环境变量相同。
# -F c 出自定义格式：恢复走 pg_restore，可以只挑部分表，比纯 SQL dump 灵活。
param(
    [string]$PgDump = "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
    [string]$Database = "boltpreload",
    [string]$User = "boltpreload",
    [string]$HostName = "127.0.0.1",
    [string]$BackupDir = "C:\BoltPreload\backups",
    # 滚动保留份数：本次备份写成后只留最近 N 份，更旧的删掉（0 = 不清理）
    [int]$KeepCount = 14,
    [string]$PgPassword = ""
)

$ErrorActionPreference = "Stop"

if ($PgPassword -ne "") {
    $env:PGPASSWORD = $PgPassword
}

if (!(Test-Path $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outFile = Join-Path $BackupDir "$Database-$timestamp.dump"

& $PgDump -h $HostName -U $User -d $Database -F c -f $outFile

# pg_dump 失败时可能留下半截文件——留着会被当成可用备份，比没有更危险，删掉再报错
if ($LASTEXITCODE -ne 0) {
    if (Test-Path $outFile) { Remove-Item $outFile }
    throw "pg_dump failed with exit code $LASTEXITCODE"
}
if (!(Test-Path $outFile)) {
    throw "Backup failed: $outFile not created"
}

Write-Host "Backup written: $outFile"

# 滚动删除：文件名带 yyyyMMdd-HHmmss，按名字倒排即按时间倒排
if ($KeepCount -gt 0) {
    $stale = Get-ChildItem -Path $BackupDir -Filter "$Database-*.dump" |
        Sort-Object Name -Descending |
        Select-Object -Skip $KeepCount
    foreach ($f in $stale) {
        Remove-Item $f.FullName
        Write-Host "Removed old backup: $($f.Name)"
    }
}
