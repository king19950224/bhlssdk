# 开机拉起整套监测栈：Docker Desktop → postgres 容器 → API(5085) → 前端 Vite(5173)。
# 背景：2026-08-17 晚整机重启后四样服务全部停摆 20 小时，趋势图只是一条平线，无人察觉
# 监测自身已失效。本脚本由“启动”文件夹的 BoltPreloadStack.vbs 在用户登录时静默调用。
# 幂等：已在跑的组件跳过；PowerShell 5.1 兼容（不用 ??/三元）。
$ErrorActionPreference = 'Continue'
$root = 'D:\api\bhlssdk'
$logDir = Join-Path $root 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$log = Join-Path $logDir 'stack-start.log'

function Log([string]$msg) {
    $line = ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg)
    Add-Content -Path $log -Value $line
}

function PortListening([int]$port) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    return ($null -ne $c)
}

Log '===== 开机自启开始 ====='

# 1) Docker 引擎（postgres 依赖它；Docker Desktop 启动最慢，先拉起）
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Log 'Docker 引擎未运行，启动 Docker Desktop'
    $dd = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    if (Test-Path $dd) { Start-Process $dd } else { Log "找不到 $dd" }
    $deadline = (Get-Date).AddSeconds(240)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        docker info *> $null
        if ($LASTEXITCODE -eq 0) { break }
    }
}
docker info *> $null
if ($LASTEXITCODE -ne 0) { Log 'Docker 引擎 240 秒内未就绪，放弃（API 无库会持续降级）' }

# 2) postgres 容器（容器已配 --restart unless-stopped，引擎起来通常会自动带起；此处兜底）
docker start boltpreload-pg *> $null
$deadline = (Get-Date).AddSeconds(90)
$pgReady = $false
while ((Get-Date) -lt $deadline) {
    docker exec boltpreload-pg pg_isready -U boltpreload *> $null
    if ($LASTEXITCODE -eq 0) { $pgReady = $true; break }
    Start-Sleep -Seconds 3
}
Log ('postgres 就绪: {0}' -f $pgReady)

# 3) API（跑已编译产物；--no-build 避免登录时触发编译占满 CPU/被文件锁卡死）
if (PortListening 5085) {
    Log 'API 5085 已在监听，跳过'
} else {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
        -ArgumentList 'run', '--project', 'src/BoltPreload.Api', '--no-build', '--no-launch-profile', '--urls', 'http://localhost:5085' `
        -WorkingDirectory $root -WindowStyle Hidden
    Log 'API 已拉起 (5085)'
}

# 4) 前端 Vite
if (PortListening 5173) {
    Log '前端 5173 已在监听，跳过'
} else {
    Start-Process -FilePath "$env:ComSpec" -ArgumentList '/c', 'npm run dev' `
        -WorkingDirectory (Join-Path $root 'web') -WindowStyle Hidden
    Log '前端已拉起 (5173)'
}

Log '===== 开机自启完成 ====='
