<#
.SYNOPSIS
    按账本执行数据库迁移（装机与现场升级共用）。

.DESCRIPTION
    用 schema_migrations 账本记录已执行的迁移文件，重复执行自动跳过：
    升级现网时直接重跑本脚本，不需要人工比对这次新增了哪几个 SQL。
    此前手册里的裸循环要求每个迁移自身幂等，而 0001 等早期迁移并不幂等，
    对存量库重跑会在第一个 CREATE TABLE 上失败。

    每个迁移文件整体包在一个事务里执行（psql --single-transaction），
    账本记录与迁移语句同一事务提交，失败整体回滚；修复后重跑从断点续跑。

    存量库（有业务表但没账本，即本脚本引入前装的机器）第一次跑必须带 -Baseline，
    显式声明"这台机器装机时执行到了哪个迁移"；≤Baseline 的文件只记账不执行。
    不带 -Baseline 时脚本会拒绝执行并给出提示，避免对存量库从 0001 重放。

    认证：psql 非交互执行需要先设环境变量 PGPASSWORD，或配好 %APPDATA%\postgresql\pgpass.conf。

.EXAMPLE
    # 全新空库 / 已有账本的库：直接跑
    .\apply-migrations.ps1

.EXAMPLE
    # 存量库首次接入账本（例：装机时手工执行到 0024）
    .\apply-migrations.ps1 -Baseline 0024
#>
param(
    [string]$Psql = "psql",
    [string]$Database = "boltpreload",
    [string]$User = "boltpreload",
    [string]$HostName = "127.0.0.1",
    [string]$SqlDir = (Join-Path $PSScriptRoot "..\sql"),
    [string]$Baseline = ""
)

$ErrorActionPreference = "Stop"

function Invoke-Sql {
    param([string[]]$ExtraArgs)
    & $Psql -h $HostName -U $User -d $Database -v ON_ERROR_STOP=1 @ExtraArgs
    if ($LASTEXITCODE -ne 0) { throw "psql 失败（exit=$LASTEXITCODE）" }
}

function Query-Scalar {
    param([string]$Sql)
    $out = & $Psql -h $HostName -U $User -d $Database -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql 查询失败：$Sql" }
    return ($out | Where-Object { $_ -ne "" } | Select-Object -First 1)
}

$files = Get-ChildItem (Join-Path $SqlDir "0*.sql") -File | Sort-Object Name
if ($files.Count -eq 0) { throw "在 $SqlDir 没找到任何 0*.sql 迁移文件" }

# 1) 账本表（幂等）
Invoke-Sql @("-q", "-c", @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    filename   text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
)
"@)

# 2) 存量库守卫：没账本记录、但业务表已存在 → 必须显式给 -Baseline
$appliedRaw = & $Psql -h $HostName -U $User -d $Database -t -A -c "SELECT filename FROM schema_migrations"
if ($LASTEXITCODE -ne 0) { throw "读取 schema_migrations 失败" }
$applied = @($appliedRaw | Where-Object { $_ -ne "" })

if ($applied.Count -eq 0 -and -not $Baseline) {
    $hasSchema = Query-Scalar "SELECT to_regclass('public.measurements') IS NOT NULL"
    if ($hasSchema -eq "t") {
        throw ("这是一个已有 schema 但没有迁移账本的存量库。" +
            "请先确认装机时执行到了哪个迁移（通常是交付版本自带的最后一个序号），" +
            "然后带 -Baseline 重跑，例如：.\apply-migrations.ps1 -Baseline 0024")
    }
}

# 3) Baseline 记账：≤ 指定序号的文件标记为已执行（不真正执行）
if ($Baseline) {
    if ($Baseline -notmatch '^\d{4}$') { throw "-Baseline 必须是 4 位序号，例如 0024" }
    foreach ($f in $files) {
        $seq = $f.Name.Substring(0, 4)
        if ($seq -le $Baseline -and $applied -notcontains $f.Name) {
            Invoke-Sql @("-q", "-c",
                "INSERT INTO schema_migrations(filename) VALUES ('$($f.Name)') ON CONFLICT DO NOTHING")
            Write-Host "[migrate] 记账（不执行）: $($f.Name)"
        }
    }
    # 重新读一遍账本，后续应用阶段以账本为准
    $appliedRaw = & $Psql -h $HostName -U $User -d $Database -t -A -c "SELECT filename FROM schema_migrations"
    if ($LASTEXITCODE -ne 0) { throw "读取 schema_migrations 失败" }
    $applied = @($appliedRaw | Where-Object { $_ -ne "" })
}

# 4) 按序应用未记账的迁移：文件 + 账本 INSERT 同一事务，失败整体回滚
$appliedCount = 0
foreach ($f in $files) {
    if ($applied -contains $f.Name) {
        Write-Host "[migrate] 跳过（已应用）: $($f.Name)"
        continue
    }
    Write-Host "[migrate] 应用: $($f.Name)"
    & $Psql -h $HostName -U $User -d $Database -v ON_ERROR_STOP=1 --single-transaction `
        -f $f.FullName `
        -c "INSERT INTO schema_migrations(filename) VALUES ('$($f.Name)')"
    if ($LASTEXITCODE -ne 0) {
        throw "迁移失败：$($f.Name)（该文件已整体回滚；修复问题后重跑本脚本，会从这里续跑）"
    }
    $appliedCount++
}

Write-Host "[migrate] 完成：本次应用 $appliedCount 个迁移，账本共 $($applied.Count + $appliedCount) 条。"
