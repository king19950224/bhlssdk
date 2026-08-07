<#
.SYNOPSIS
    把 BoltPreload.Api 注册为 Windows 服务，并一次性把现场配置落好。

.DESCRIPTION
    此前本脚本只注册服务、不写任何配置，现场只能靠人另外去设环境变量 —— 漏一项的后果很重：
      - 漏 BootstrapAdminPassword：空库里没有任何用户，建账号接口又要求 admin 鉴权，
        装完没人能登录；
      - 漏 License 公钥：授权判无效，监测照跑但所有写操作 403，系统"能看不能用"；
      - 把 ASPNETCORE_ENVIRONMENT 设成 Development：启用硬编码 dev JWT 密钥 + 自动建 admin/admin123。
    因此改为：必填项缺失直接拒绝安装，并由脚本统一写好配置。

    配置分两处放，按是否敏感区分：
      - 敏感项（连接串、JWT 密钥、首个管理员密码）→ 服务级环境变量（NSSM AppEnvironmentExtra）
      - 非敏感项（License 公钥 PEM、Fx 模式、设备地址）→ appsettings.Production.json
        （PEM 含换行，不适合塞进环境变量）

.EXAMPLE
    .\install-service.ps1 `
        -ConnectionString "Host=localhost;Port=5432;Database=boltpreload;Username=boltpreload;Password=xxx" `
        -JwtSigningKey "<64 字节随机串>" `
        -LicensePublicKeyPemPath "C:\BoltPreload\license-public.pem" `
        -BootstrapAdminPassword "<首个管理员密码>" `
        -DeviceCommParams "192.168.1.50:23"

.NOTES
    完整现场步骤见 资料/工程文档/deployment-guide.md。
#>
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [Parameter(Mandatory = $true)][string]$JwtSigningKey,
    [Parameter(Mandatory = $true)][string]$LicensePublicKeyPemPath,

    # 首装用：建出第一个 admin。建好后建议重跑一次本脚本且不带该参数，把它从服务配置里移除。
    [string]$BootstrapAdminPassword = "",

    # 现场设备地址。留空则沿用 appsettings.json 里的 Fx:Real:CommParams —— 那是实验室地址，
    # 现场不改会一直连不上。多设备请改为往 devices 表写记录。
    [string]$DeviceCommParams = "",

    # 真机 MultiMeas 不随测量带波形；需要波形诊断/A-Scan 时开启
    [switch]$EnableWaveSampling,

    [string]$Urls = "http://127.0.0.1:5000",
    [int]$FirewallPort = 5000,
    [switch]$OpenFirewall,

    [string]$ServiceName = "BoltPreload.Api",
    [string]$DisplayName = "Bolt Preload Monitoring Service",
    [string]$InstallDir = "C:\BoltPreload",
    [string]$ExeName = "BoltPreload.Api.exe",
    [string]$NssmPath = "C:\BoltPreload\tools\nssm.exe"
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------- 前置检查

$exePath = Join-Path $InstallDir $ExeName
if (!(Test-Path $exePath)) {
    throw "找不到可执行文件：$exePath（请先把 dotnet publish 的产物复制到 $InstallDir）"
}

# 前端产物缺失是"装完打开是空白页"的直接原因，装之前就拦住
$indexPath = Join-Path $InstallDir "wwwroot\index.html"
if (!(Test-Path $indexPath)) {
    throw "缺少前端产物：$indexPath。发布前需先在 web/ 执行 npm run build，再 dotnet publish。"
}

if (!(Test-Path $NssmPath)) {
    throw "找不到 NSSM：$NssmPath。请把 nssm.exe 放到 $InstallDir\tools\ 或用 -NssmPath 指定。"
}

if (!(Test-Path $LicensePublicKeyPemPath)) {
    throw "找不到 License 公钥：$LicensePublicKeyPemPath（由 tools/LicenseSigner keygen 生成）"
}

if ($JwtSigningKey.Length -lt 32) {
    throw "Jwt 签名密钥过短（当前 $($JwtSigningKey.Length) 字节），至少 32 字节，建议 64 字节随机串。"
}

$licensePath = Join-Path $InstallDir "license.json"
if (!(Test-Path $licensePath)) {
    Write-Warning "未发现 $licensePath —— 授权将判为无效：监测会照常运行，但建档/标定/告警确认等写操作一律被拒(403)。请按 资料/工程文档/deployment-guide.md §4 签发后放入。"
}

# ---------------------------------------------------------------- 站点配置文件

$publicKeyPem = Get-Content -Path $LicensePublicKeyPemPath -Raw

$siteConfig = [ordered]@{
    "//"    = "本文件由 deploy/windows/install-service.ps1 生成，仅存放非敏感的现场配置；密钥走服务级环境变量。"
    License = [ordered]@{ PublicKeyPem = $publicKeyPem }
    Fx      = [ordered]@{ Mode = "Real" }
}

if ($DeviceCommParams -ne "") {
    $siteConfig.Fx.Real = [ordered]@{ CommParams = $DeviceCommParams }
}
if ($EnableWaveSampling) {
    $siteConfig.Ingestion = [ordered]@{ WaveSampling = [ordered]@{ Enabled = $true } }
}

$siteConfigPath = Join-Path $InstallDir "appsettings.Production.json"
$siteConfig | ConvertTo-Json -Depth 6 | Set-Content -Path $siteConfigPath -Encoding UTF8
Write-Host "已写入站点配置：$siteConfigPath"

# ---------------------------------------------------------------- 注册服务

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "服务已存在，先停止并移除：$ServiceName"
    & $NssmPath stop $ServiceName | Out-Null
    & $NssmPath remove $ServiceName confirm | Out-Null
    Start-Sleep -Seconds 2
}

& $NssmPath install $ServiceName $exePath | Out-Null
& $NssmPath set $ServiceName DisplayName $DisplayName | Out-Null
& $NssmPath set $ServiceName Description "Industrial bolt preload online monitoring service" | Out-Null
& $NssmPath set $ServiceName AppDirectory $InstallDir | Out-Null
& $NssmPath set $ServiceName Start SERVICE_AUTO_START | Out-Null

# 服务级环境变量：ASPNETCORE_ENVIRONMENT 固定为 Production —— 绝不能是 Development
$envPairs = @(
    "ASPNETCORE_ENVIRONMENT=Production"
    "ASPNETCORE_URLS=$Urls"
    "ConnectionStrings__Postgres=$ConnectionString"
    "Jwt__SigningKey=$JwtSigningKey"
)
if ($BootstrapAdminPassword -ne "") {
    $envPairs += "BoltPreload__BootstrapAdminPassword=$BootstrapAdminPassword"
}
& $NssmPath set $ServiceName AppEnvironmentExtra $envPairs | Out-Null

# 服务 stdout/stderr 兜底日志（应用自身另有 Serilog 按天滚动的 logs\boltpreload-*.log）
& $NssmPath set $ServiceName AppStdout (Join-Path $InstallDir "logs\service-stdout.log") | Out-Null
& $NssmPath set $ServiceName AppStderr (Join-Path $InstallDir "logs\service-stderr.log") | Out-Null
& $NssmPath set $ServiceName AppRotateFiles 1 | Out-Null
& $NssmPath set $ServiceName AppRotateOnline 1 | Out-Null
& $NssmPath set $ServiceName AppRotateSeconds 86400 | Out-Null
& $NssmPath set $ServiceName AppRestartDelay 10000 | Out-Null

# 防火墙：默认不开。本脚本配出的是 HTTP 明文监听，JWT 在网络上可截获——局域网访问
# 请接着跑 setup-https.ps1 定稿 HTTPS（Kestrel 配置节会接管监听地址，此后这里的
# ASPNETCORE_URLS 只剩摆设，启动日志的 Overriding 提示属预期），只给本机用才走 -OpenFirewall 放 HTTP 端口。
if ($OpenFirewall) {
    New-NetFirewallRule `
        -DisplayName "BoltPreload API $FirewallPort" `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $FirewallPort `
        -ErrorAction SilentlyContinue | Out-Null
    Write-Host "已开放入站端口 $FirewallPort"
} else {
    Write-Host "未开放防火墙端口（如需局域网访问请加 -OpenFirewall）"
}

Start-Service -Name $ServiceName
Write-Host "已安装并启动 $ServiceName（$exePath）"

# ---------------------------------------------------------------- 启动后自检

$healthUrl = ($Urls -split ';')[0].TrimEnd('/') + "/api/health/ready"
Write-Host "等待服务就绪：$healthUrl"

$ready = $false
foreach ($i in 1..30) {
    Start-Sleep -Seconds 2
    try {
        $resp = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5
        if ($resp.status -eq "ready") { $ready = $true; break }
        Write-Host "  状态=$($resp.status) 失联服务=$($resp.deadServices -join ',')"
    } catch { }
}

if (-not $ready) {
    Write-Warning "服务未在 60s 内就绪。请查看 $InstallDir\logs\ 下的日志；常见原因：数据库连不上、迁移未执行、连接串或端口不对。"
} else {
    Write-Host "就绪检查通过。"
}

Write-Host ""
Write-Host "接下来请按 资料/工程文档/deployment-guide.md §8 走一遍装完自检（尤其是：浏览器直接打开 /alarms 不应白屏、/api/license/status 应为 valid=true）。"
Write-Host "交付硬化两件事别漏：HTTPS（.\setup-https.ps1，手册 §5）与每日备份任务（.\register-backup-task.ps1 -RunNow，手册 §7.1）。"
if ($BootstrapAdminPassword -ne "") {
    Write-Host "提示：首个管理员已创建。建议登录确认后，重跑本脚本且不带 -BootstrapAdminPassword，把该密码从服务配置中移除。"
}
