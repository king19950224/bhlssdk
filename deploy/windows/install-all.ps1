<#
.SYNOPSIS
    一条命令装完整站：建库 → 迁移 → 注册服务 → HTTPS → 备份任务 → 打印机器码。

.DESCRIPTION
    把 deployment-guide §3–§8 的手工步骤编排成一次执行，各步骤仍是那几个单独验证过的脚本
    （apply-migrations / install-service / setup-https / register-backup-task），本脚本只管顺序、
    传参和前置检查——单步出问题时照旧可以拿对应脚本单独重跑。
    Inno Setup 安装包（deploy/installer/installer.iss）装完文件后调的也是本脚本，手工部署和
    一键安装走同一条编排，不维护两套逻辑。

    前置（本脚本不代装，缺了会明确报错）：
      - PostgreSQL 16 + TimescaleDB 已装好（psql/pg_dump 可用）；
      - 程序目录已就位（BoltPreload.Api.exe / wwwroot / tools\nssm.exe / license-public.pem）；
      - 管理员 PowerShell。

    口令处理：postgres 超级用户口令与首个管理员口令不给参数就交互问；应用库口令不给就
    自动生成（只含字母数字，避免连接串转义问题）并在结尾打印一次——记下来，备份任务和
    诊断排查都要用。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install-all.ps1
    # 全交互：按提示输入 postgres 口令和管理员口令，其余自动

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install-all.ps1 `
        -PostgresPassword "<pg超级口令>" -AdminPassword "<管理员口令>" `
        -DeviceCommParams "192.168.1.50:23" -HttpsIp 192.168.1.10
#>
param(
    [string]$InstallDir = "C:\BoltPreload",
    # postgres 超级用户口令（建库/建 TimescaleDB 扩展用，扩展必须超级用户建）
    [string]$PostgresPassword = "",
    # 应用库 boltpreload 用户口令；留空自动生成（字母数字 20 位）并在结尾打印
    [string]$DbPassword = "",
    # 首个管理员口令（空库没有任何用户，漏了装完没人能登录）
    [string]$AdminPassword = "",
    # 现场设备地址（如 192.168.1.50:23）；彩排机没设备可留空
    [string]$DeviceCommParams = "",
    # 编进 HTTPS 证书 SAN 的本机局域网 IP（操作员按 IP 访问就要给）
    [string]$HttpsIp = "",
    [switch]$SkipHttps,
    [switch]$SkipBackupTask,
    [string]$PgBin = "C:\Program Files\PostgreSQL\16\bin"
)

$ErrorActionPreference = "Stop"

function New-AlphanumericSecret {
    param([int]$Length)
    # 只用字母数字：口令要进连接串和命令行，分号/引号会把转义搞成一地鸡毛
    $chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789"
    $bytes = New-Object byte[] $Length
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

# ---------------------------------------------------------------- 前置检查

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "需要管理员 PowerShell（注册服务、写受信任根、开防火墙都要提权）"
}

$psql = Join-Path $PgBin "psql.exe"
if (!(Test-Path $psql)) {
    $found = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -eq $found) {
        throw "找不到 psql（$psql 也不存在）：先安装 PostgreSQL 16 + TimescaleDB，或用 -PgBin 指定 bin 目录"
    }
    $psql = $found.Source
}

foreach ($required in @(
    (Join-Path $InstallDir "BoltPreload.Api.exe"),
    (Join-Path $InstallDir "wwwroot\index.html"),
    (Join-Path $InstallDir "tools\nssm.exe"),
    (Join-Path $InstallDir "license-public.pem"))) {
    if (!(Test-Path $required)) {
        throw "缺少 $required —— 程序目录没摆全（交付包 app\ 拷到 $InstallDir，nssm.exe 放 tools\，公钥放根目录）"
    }
}

# ---------------------------------------------------------------- 补齐口令

if ($PostgresPassword -eq "") {
    $PostgresPassword = Read-Host "postgres 超级用户口令（建库与建扩展用）"
}
if ($AdminPassword -eq "") {
    $AdminPassword = Read-Host "首个管理员（admin）口令"
}
$dbPasswordGenerated = $false
if ($DbPassword -eq "") {
    $DbPassword = New-AlphanumericSecret -Length 20
    $dbPasswordGenerated = $true
} elseif ($DbPassword -match "[;'`"]") {
    throw "应用库口令不要含分号/引号（要进连接串与命令行）；留空可自动生成安全口令"
}

# ---------------------------------------------------------------- 1) 建库（幂等，重跑安全）

Write-Host "== 1/6 建库与角色 =="
$env:PGPASSWORD = $PostgresPassword
$escapedDbPwd = $DbPassword.Replace("'", "''")

# 角色存在则只重设口令（重装场景），不存在则建
& $psql -U postgres -h 127.0.0.1 -v ON_ERROR_STOP=1 -q -c @"
DO `$`$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'boltpreload') THEN
        ALTER ROLE boltpreload LOGIN PASSWORD '$escapedDbPwd';
    ELSE
        CREATE ROLE boltpreload LOGIN PASSWORD '$escapedDbPwd';
    END IF;
END
`$`$;
"@
if ($LASTEXITCODE -ne 0) { throw "建角色失败（postgres 口令不对？服务没起？）" }

$dbExists = & $psql -U postgres -h 127.0.0.1 -t -A -c "SELECT 1 FROM pg_database WHERE datname = 'boltpreload'"
if ($LASTEXITCODE -ne 0) { throw "查询数据库存在性失败" }
if ($dbExists -ne "1") {
    & $psql -U postgres -h 127.0.0.1 -v ON_ERROR_STOP=1 -q -c "CREATE DATABASE boltpreload OWNER boltpreload"
    if ($LASTEXITCODE -ne 0) { throw "建库失败" }
}

# TimescaleDB 扩展必须超级用户建（迁移脚本用普通用户跑，0001 里的 CREATE EXTENSION 会因权限报错）
& $psql -U postgres -h 127.0.0.1 -d boltpreload -v ON_ERROR_STOP=1 -q -c "CREATE EXTENSION IF NOT EXISTS timescaledb;"
if ($LASTEXITCODE -ne 0) { throw "建 TimescaleDB 扩展失败：确认装的是带 TimescaleDB 的 PostgreSQL（shared_preload_libraries 已配）" }

# ---------------------------------------------------------------- 2) 迁移（账本式，重跑自动跳过）

Write-Host "== 2/6 数据库迁移 =="
$env:PGPASSWORD = $DbPassword
& (Join-Path $PSScriptRoot "apply-migrations.ps1") -Psql $psql

# ---------------------------------------------------------------- 3) 注册服务

Write-Host "== 3/6 注册 Windows 服务 =="
$jwt = New-AlphanumericSecret -Length 64
$serviceArgs = @{
    ConnectionString         = "Host=localhost;Port=5432;Database=boltpreload;Username=boltpreload;Password=$DbPassword"
    JwtSigningKey            = $jwt
    LicensePublicKeyPemPath  = (Join-Path $InstallDir "license-public.pem")
    BootstrapAdminPassword   = $AdminPassword
    InstallDir               = $InstallDir
    NssmPath                 = (Join-Path $InstallDir "tools\nssm.exe")
}
if ($DeviceCommParams -ne "") { $serviceArgs.DeviceCommParams = $DeviceCommParams }
& (Join-Path $PSScriptRoot "install-service.ps1") @serviceArgs

# ---------------------------------------------------------------- 4) HTTPS

if ($SkipHttps) {
    Write-Host "== 4/6 HTTPS：跳过（-SkipHttps）——纯本机诊断可以，局域网访问是明文，别这么交付 =="
} else {
    Write-Host "== 4/6 HTTPS 定稿 =="
    $httpsArgs = @{ InstallDir = $InstallDir; OpenFirewall = $true }
    if ($HttpsIp -ne "") { $httpsArgs.IpAddresses = @($HttpsIp) }
    & (Join-Path $PSScriptRoot "setup-https.ps1") @httpsArgs
}

# ---------------------------------------------------------------- 5) 备份计划任务

if ($SkipBackupTask) {
    Write-Host "== 5/6 备份任务：跳过（-SkipBackupTask）——没有定时备份的库等着丢数据，别这么交付 =="
} else {
    Write-Host "== 5/6 每日备份计划任务 =="
    & (Join-Path $PSScriptRoot "register-backup-task.ps1") `
        -PgDump (Join-Path $PgBin "pg_dump.exe") -PgPassword $DbPassword -RunNow
}

# ---------------------------------------------------------------- 6) 机器码与收尾

Write-Host "== 6/6 完成 =="
$machineCode = $null
try {
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/license/machine-code" -TimeoutSec 10
    $machineCode = $resp | ConvertTo-Json -Compress
} catch {
    Write-Warning "取机器码失败（服务未就绪？稍后手动执行 curl.exe http://127.0.0.1:5000/api/license/machine-code）"
}

Write-Host ""
Write-Host "================= 装机完成，抄下这一段 ================="
if ($dbPasswordGenerated) {
    Write-Host "数据库口令（自动生成，只显示这一次）：$DbPassword"
}
if ($machineCode) {
    Write-Host "机器码响应：$machineCode"
}
Write-Host "下一步：把机器码发给供应商签发 license.json → 放到 $InstallDir\license.json → Restart-Service BoltPreload.Api"
Write-Host "自检：照 docs\deployment-guide.md §8 清单走一遍（浏览器开 /alarms 不白屏、license status valid=true）"
Write-Host "========================================================"
