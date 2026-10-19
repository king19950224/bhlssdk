# 一键发布：桌面端 + 大屏 + 页面 + farm.json + 标定文件，全部放进一个固定文件夹，拷走就能用。
#   .\publish.ps1            → dist\螺栓预紧力监测\   （固定输出目录，每次发布都是这一个）
#   .\publish.ps1 -Out D:\发布\xxx
# 两个 exe 都是自包含单文件（目标机不用装 .NET）。同一个文件夹里两边共用 wwwroot\farm.json 和 .ucf：
# 桌面端找不到自己旁边的 farm.json 时会读 wwwroot\farm.json，现场配置对话框也只写这一份。
# 本文件必须保存为 UTF-8 带 BOM：Windows PowerShell 5.1 读无 BOM 的 UTF-8 会把中文字符串拆坏、整段解析失败。
param([string]$Out = (Join-Path $PSScriptRoot "dist\螺栓预紧力监测"))

$ErrorActionPreference = "Stop"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }

$outFull = [IO.Path]::GetFullPath($Out).TrimEnd('\')

# 这台机器上如果装着大屏服务、而且服务的 exe 就在发布目标里，先停下来（exe 锁着删不掉），发完再起；装在别的目录的服务不动。
# 停的时候先把服务置为"禁用"：服务的失败恢复策略会在进程被杀后 5 秒自动拉起，正好和拷文件撞车；禁用期间它不会自启。
# 下面所有步骤包在 try/finally 里：发布中途出错也要把服务改回"自动"——否则机器上留着一个被禁用、起不来的服务，
# 桌面端「打开大屏」按 sc start 会得到 1058，现场不知道为什么
$svc = Get-Service BoltPreloadMonitor -ErrorAction SilentlyContinue
$restartService = $false
$stash = $null
$siteFarm = Join-Path $Out "wwwroot\farm.json"
if ($svc) {
    $svcPath = (Get-CimInstance Win32_Service -Filter "Name='BoltPreloadMonitor'" -ErrorAction SilentlyContinue).PathName
    $svcExe = if ($svcPath) { ($svcPath -replace '^\s*"([^"]+)".*$', '$1') -replace '^\s*(\S+\.exe).*$', '$1' } else { $null }
    $svcInTarget = $svcExe -and ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($svcExe)).TrimEnd('\') -ieq $outFull)
    if (-not $svcInTarget) {
        Write-Host "大屏服务装在别的目录（$svcExe），本次发布不碰它"
        $svc = $null
    }
}
if ($svc) {
    Write-Host "大屏服务已安装，发布期间先停止……"
    sc.exe config BoltPreloadMonitor start= disabled | Out-Null
    $restartService = $true
    if ($svc.Status -ne "Stopped") { Stop-Service BoltPreloadMonitor -Force -ErrorAction SilentlyContinue }
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Process BoltPreload.Monitor -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    Get-Process BoltPreload.Monitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
}

try {
    # 服务已经停过。目标里还有别的 FxMonitor / 大屏进程（例如双击开着的桌面端）时，exe 删不掉，先退出，finally 会把服务改回自动并拉起。
    # 服务进程的 Path 有时读不到（权限不够），单靠进程列表会漏判，于是先删了网页目录、exe 却删不掉。先试着改名，改不动就一行都不删。
    $busy = Get-Process FxMonitor, BoltPreload.Monitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetDirectoryName($_.Path).TrimEnd('\') -ieq $outFull }
    if ($busy) { throw "发布目标里的程序正在运行（$(($busy | ForEach-Object { "$($_.Name) PID $($_.Id)" }) -join '、')），请先关掉它再发布" }
    foreach ($lockedName in @("BoltPreload.Monitor.exe", "FxMonitor.exe")) {
        $locked = Join-Path $Out $lockedName
        if (-not (Test-Path -LiteralPath $locked)) { continue }
        $probe = "$locked.publish-lock-test"
        try {
            [IO.File]::Move($locked, $probe)
            [IO.File]::Move($probe, $locked)
        } catch {
            if (Test-Path -LiteralPath $probe) { [IO.File]::Move($probe, $locked) }
            throw "发布目标里的 $lockedName 正被占用，当前权限停不掉它。现场文件没有删。请用管理员身份运行本脚本。"
        }
    }

    # 目标目录里的现场数据不能随发布丢：历史库 data\、日志 logs\、桌面端 settings.json 和 操作记录.log 不清；
    # 现场改过的 wwwroot\farm.json 和放进去的 .ucf 先收起来，发完再放回去（仓库里那份 farm.json 只是初始模板）
    $keep = @("data", "logs", "settings.json", "操作记录.log")
    $stash = Join-Path ([IO.Path]::GetTempPath()) ("bp-publish-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory $stash | Out-Null
    if (Test-Path $siteFarm) { Copy-Item $siteFarm (Join-Path $stash "farm.json") }
    if (Test-Path $Out) { Get-ChildItem $Out -Filter *.ucf | Copy-Item -Destination $stash }

    if (Test-Path $Out) {
        Get-ChildItem $Out -Force | Where-Object { $_.Name -notin $keep } | Remove-Item -Recurse -Force -ErrorAction Stop
    } else {
        New-Item -ItemType Directory $Out | Out-Null
    }

    Write-Host "发布大屏 BoltPreload.Monitor ..."
    & $dotnet publish (Join-Path $PSScriptRoot "src\BoltPreload.Monitor") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $Out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "大屏发布失败" }

    Write-Host "发布桌面端 FxMonitor ..."
    & $dotnet publish (Join-Path $PSScriptRoot "src\FxMonitor") -c Release -p:DebugType=none -o $Out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "桌面端发布失败" }

    # 只留 wwwroot\farm.json 一份（桌面端的链接副本删掉，免得两份各改各的）；调试符号、临时设置、IIS/静态资源清单这些发布副产物不发
    Remove-Item (Join-Path $Out "farm.json") -ErrorAction SilentlyContinue
    Get-ChildItem $Out -Include *.pdb, appsettings*.json, web.config, *.staticwebassets.endpoints.json -Recurse -Depth 0 |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # 业主机上装/卸 Windows 服务的两个脚本
    foreach ($f in "安装大屏服务.cmd", "卸载大屏服务.cmd") {
        Copy-Item (Join-Path $PSScriptRoot "deploy\$f") $Out
    }

    Write-Host ""
    Write-Host "发布完成：$Out"
    Get-ChildItem $Out | ForEach-Object { "  {0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
    Write-Host ""
    Write-Host "现场：整个文件夹拷到大屏电脑，右键「安装大屏服务.cmd」以管理员身份运行一次——开机自起、没有窗口、崩了自动重启。"
    Write-Host "      调试/置零/配置用 FxMonitor.exe，「打开大屏」按钮开网页。"
}
finally {
    # 放回现场的 farm.json 和 .ucf，编译失败也要放回
    if ($stash -and (Test-Path $stash)) {
        try {
            if (Test-Path (Join-Path $stash "farm.json")) {
                New-Item -ItemType Directory (Split-Path $siteFarm) -Force | Out-Null
                Copy-Item (Join-Path $stash "farm.json") $siteFarm -Force
                Write-Host "已保留现场的 wwwroot\farm.json"
            }
            Get-ChildItem $stash -Filter *.ucf | ForEach-Object { if (-not (Test-Path (Join-Path $Out $_.Name))) { Copy-Item $_.FullName $Out } }
            Remove-Item $stash -Recurse -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Warning "现场的 farm.json / .ucf 没能放回，备份在 $stash"
        }
    }

    if ($restartService) {
        # 不管发布成没成，启动类型都改回自动；exe 在才起服务——发布半截 exe 没了，起了也只会被恢复策略反复拉起
        sc.exe config BoltPreloadMonitor start= auto | Out-Null
        if (Test-Path (Join-Path $Out "BoltPreload.Monitor.exe")) {
            Write-Host "重新启动大屏服务……"
            Start-Service BoltPreloadMonitor -ErrorAction Continue
        } else {
            Write-Warning "发布没有完成，$Out 里没有 BoltPreload.Monitor.exe，大屏服务未启动；修好后重新发布，或手工 Start-Service BoltPreloadMonitor"
        }
    }
}
