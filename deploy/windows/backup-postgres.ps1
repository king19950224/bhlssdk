# 非交互跑必须能拿到口令：设 PGPASSWORD、配好 pgpass.conf，或直接传 -PgPassword。
# 计划任务跑在 SYSTEM 账户时 pgpass.conf 的路径见 deployment-guide §7.1；-PgPassword 会出现在
# 任务定义里（本机管理员可读），信任边界与 NSSM 服务环境变量相同。
# -F c 出自定义格式：恢复走 pg_restore，可以只挑部分表，比纯 SQL dump 灵活。
#
# 保留策略两级：日备滚动 KeepCount 份 + 每月首个备份晋级 monthly\ 保留 KeepMonthly 份。
# 测量数据合同保留 5 年，只有 14 天滚动的话库一坏 5 年历史就没了——月备把恢复点拉长到一年量级；
# 异地（NAS/业主存储）仍需在交接时确定同步 $BackupDir 的方式，本脚本不越权做网络传输。
param(
    [string]$PgDump = "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
    [string]$Database = "boltpreload",
    [string]$User = "boltpreload",
    [string]$HostName = "127.0.0.1",
    [string]$BackupDir = "C:\BoltPreload\backups",
    # 日备滚动保留份数：本次备份写成后只留最近 N 份，更旧的删掉（0 = 不清理）
    [int]$KeepCount = 30,
    # 月备保留份数（monthly\ 子目录；0 = 不做月备晋级）
    [int]$KeepMonthly = 12,
    # 一并归档运行数据（data\wal 残留 + 报告快照；波形体积大且 30 天自滚动，不进日备）
    [string]$DataDir = "",
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

# 月备晋级：本月还没有月备就把这份拷进 monthly\（先拷后清，日备滚动不影响月备）
if ($KeepMonthly -gt 0) {
    $monthlyDir = Join-Path $BackupDir "monthly"
    if (!(Test-Path $monthlyDir)) { New-Item -ItemType Directory -Path $monthlyDir | Out-Null }
    $monthTag = Get-Date -Format "yyyyMM"
    $existing = Get-ChildItem -Path $monthlyDir -Filter "$Database-$monthTag*.dump" -ErrorAction SilentlyContinue
    if (-not $existing) {
        Copy-Item $outFile (Join-Path $monthlyDir (Split-Path $outFile -Leaf))
        Write-Host "Monthly backup promoted: $monthTag"
        $staleMonthly = Get-ChildItem -Path $monthlyDir -Filter "$Database-*.dump" |
            Sort-Object Name -Descending | Select-Object -Skip $KeepMonthly
        foreach ($f in $staleMonthly) {
            Remove-Item $f.FullName
            Write-Host "Removed old monthly backup: $($f.Name)"
        }
    }
}

# 运行数据归档（可选）：WAL 残留是断网期未补写的测量、报告快照是签发档案，都不在 pg_dump 里
if ($DataDir -ne "" -and (Test-Path $DataDir)) {
    $dataZip = Join-Path $BackupDir "data-$timestamp.zip"
    $targets = @("wal", "reports") | ForEach-Object { Join-Path $DataDir $_ } | Where-Object { Test-Path $_ }
    if ($targets) {
        Compress-Archive -Path $targets -DestinationPath $dataZip -CompressionLevel Optimal
        Write-Host "Data archive written: $dataZip"
        $staleZip = Get-ChildItem -Path $BackupDir -Filter "data-*.zip" |
            Sort-Object Name -Descending | Select-Object -Skip ([Math]::Max($KeepCount, 1))
        foreach ($f in $staleZip) { Remove-Item $f.FullName }
    }
}

# 日备滚动删除：文件名带 yyyyMMdd-HHmmss，按名字倒排即按时间倒排（monthly\ 子目录不受影响）
if ($KeepCount -gt 0) {
    $stale = Get-ChildItem -Path $BackupDir -Filter "$Database-*.dump" |
        Sort-Object Name -Descending |
        Select-Object -Skip $KeepCount
    foreach ($f in $stale) {
        Remove-Item $f.FullName
        Write-Host "Removed old backup: $($f.Name)"
    }
}
