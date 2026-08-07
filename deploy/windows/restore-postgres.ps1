<#
.SYNOPSIS
    从 pg_dump 自定义格式备份恢复数据库（TimescaleDB 专用流程，一条命令走完）。

.DESCRIPTION
    Timescale 的 hypertable 不能拿普通 pg_restore 直接怼：恢复前后必须调
    timescaledb_pre_restore() / timescaledb_post_restore()，否则 chunk 元数据错乱。
    此前手册 §7.1 只有手工步骤，现场压力下逐条敲命令是恢复演练里最容易错的环节——收成脚本。

    默认恢复到"演练库"（-TargetDatabase boltpreload_drill）：先验证备份可用、抽查行数，
    不碰生产库。真恢复生产时显式给 -TargetDatabase boltpreload -ReplaceExisting，
    脚本会先停 BoltPreload.Api 服务（写连接不断开 DROP 会挂住）。

.EXAMPLE
    # 恢复演练（默认建 boltpreload_drill，验完自己删）
    .\restore-postgres.ps1 -DumpFile C:\BoltPreload\backups\boltpreload-20260807-020000.dump

    # 真恢复生产库（覆盖！确认备份时间点再动手）
    .\restore-postgres.ps1 -DumpFile ... -TargetDatabase boltpreload -ReplaceExisting
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DumpFile,
    [string]$PgBin = "C:\Program Files\PostgreSQL\16\bin",
    [string]$User = "boltpreload",
    [string]$HostName = "127.0.0.1",
    [int]$Port = 5432,
    [string]$TargetDatabase = "boltpreload_drill",
    [switch]$ReplaceExisting,
    [switch]$DropDrillAfterCheck,
    [string]$PgPassword = ""
)

$ErrorActionPreference = "Stop"
if ($PgPassword -ne "") { $env:PGPASSWORD = $PgPassword }
if (-not (Test-Path $DumpFile)) { throw "备份文件不存在：$DumpFile" }

$psql = Join-Path $PgBin "psql.exe"
$pgRestore = Join-Path $PgBin "pg_restore.exe"
foreach ($exe in @($psql, $pgRestore)) {
    if (-not (Test-Path $exe)) { throw "未找到 $exe（-PgBin 指向 PostgreSQL bin 目录）" }
}

function Invoke-Psql {
    param([string]$Db, [string]$Sql)
    & $psql -h $HostName -p $Port -U $User -d $Db -v ON_ERROR_STOP=1 -q -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql 失败：$Sql" }
}

# 1) 目标库处置
$exists = & $psql -h $HostName -p $Port -U $User -d postgres -t -A -c "SELECT 1 FROM pg_database WHERE datname='$TargetDatabase'"
if ($exists -eq "1") {
    if (-not $ReplaceExisting) {
        throw "目标库 $TargetDatabase 已存在。演练请换名或加 -ReplaceExisting；恢复生产库属破坏性操作，必须显式 -ReplaceExisting"
    }
    if ($TargetDatabase -eq "boltpreload") {
        $svc = Get-Service -Name "BoltPreload.Api" -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq "Running") {
            Write-Host "[restore] 停止 BoltPreload.Api（恢复期间不得有写连接）"
            Stop-Service -Name "BoltPreload.Api" -Force
        }
    }
    Write-Host "[restore] 删除既有库 $TargetDatabase"
    Invoke-Psql -Db "postgres" -Sql "DROP DATABASE $TargetDatabase WITH (FORCE)"
}

Write-Host "[restore] 建库 $TargetDatabase"
Invoke-Psql -Db "postgres" -Sql "CREATE DATABASE $TargetDatabase"

# 2) Timescale pre_restore → pg_restore → post_restore
Write-Host "[restore] CREATE EXTENSION timescaledb + pre_restore"
Invoke-Psql -Db $TargetDatabase -Sql "CREATE EXTENSION IF NOT EXISTS timescaledb"
Invoke-Psql -Db $TargetDatabase -Sql "SELECT timescaledb_pre_restore()"

Write-Host "[restore] pg_restore $DumpFile"
& $pgRestore -h $HostName -p $Port -U $User -d $TargetDatabase --no-owner $DumpFile
# pg_restore 对无害告警（如扩展成员对象）也会返回非零；致命错误会在 post_restore/抽查处暴露，
# 这里只提示不中断——中断反而会把库留在 pre_restore 状态
if ($LASTEXITCODE -ne 0) { Write-Warning "pg_restore 退出码 $LASTEXITCODE（有告警；若后续抽查通过可接受）" }

Invoke-Psql -Db $TargetDatabase -Sql "SELECT timescaledb_post_restore()"

# 3) 抽查：核心表行数 + 迁移账本 + 最新测量时间，给出可写进演练记录的数字
Write-Host "[restore] 抽查"
$checks = [ordered]@{
    "measurements 行数"   = "SELECT count(*) FROM measurements"
    "bolts 行数"          = "SELECT count(*) FROM bolts"
    "alarms 行数"         = "SELECT count(*) FROM alarms"
    "audit_log 行数"      = "SELECT count(*) FROM audit_log"
    "最新测量时间"        = "SELECT COALESCE(max(ts)::text, '-') FROM measurements"
    "迁移账本条数"        = "SELECT count(*) FROM schema_migrations"
}
foreach ($k in $checks.Keys) {
    $v = Invoke-Psql -Db $TargetDatabase -Sql $checks[$k]
    Write-Host ("  {0}: {1}" -f $k, $v)
}

if ($TargetDatabase -eq "boltpreload" -and $ReplaceExisting) {
    $svc = Get-Service -Name "BoltPreload.Api" -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host "[restore] 重启 BoltPreload.Api"
        Start-Service -Name "BoltPreload.Api"
    }
}

if ($DropDrillAfterCheck -and $TargetDatabase -ne "boltpreload") {
    Write-Host "[restore] 演练完成，删除 $TargetDatabase"
    Invoke-Psql -Db "postgres" -Sql "DROP DATABASE $TargetDatabase WITH (FORCE)"
}

Write-Host "[restore] 完成。演练记录把上面的抽查数字抄进去。"
