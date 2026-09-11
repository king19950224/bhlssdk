# 一键发布：桌面端 + 大屏 + 页面 + farm.json + 标定文件，全部放进一个文件夹，拷走就能用。
#   .\publish.ps1            → dist\螺栓预紧力监测\
#   .\publish.ps1 -Out D:\发布\xxx
# 两个 exe 都是自包含单文件（目标机不用装 .NET）。同一个文件夹里两边共用 wwwroot\farm.json 和 .ucf：
# 桌面端找不到自己旁边的 farm.json 时会读 wwwroot\farm.json，现场配置对话框也只写这一份。
param([string]$Out = (Join-Path $PSScriptRoot "dist\螺栓预紧力监测"))

$ErrorActionPreference = "Stop"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }

# 这台机器上如果装着大屏服务，先停下来（exe 锁着删不掉），发完再起。
# 停的时候先把服务置为"禁用"：服务的失败恢复策略会在进程被杀后 5 秒自动拉起，正好和拷文件撞车；禁用期间它不会自启
$svc = Get-Service BoltPreloadMonitor -ErrorAction SilentlyContinue
$restartService = $false
if ($svc) {
    Write-Host "大屏服务已安装，发布期间先停止……"
    sc.exe config BoltPreloadMonitor start= disabled | Out-Null
    if ($svc.Status -ne "Stopped") { Stop-Service BoltPreloadMonitor -Force -ErrorAction SilentlyContinue }
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Process BoltPreload.Monitor -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    Get-Process BoltPreload.Monitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
    $restartService = $true
}

# 目标目录里的现场数据不能随发布丢：历史库 data\、日志 logs\、桌面端 settings.json 和 操作记录.log 不清；
# 现场改过的 wwwroot\farm.json 和放进去的 .ucf 先收起来，发完再放回去（仓库里那份 farm.json 只是初始模板）
$keep = @("data", "logs", "settings.json", "操作记录.log")
$stash = Join-Path ([IO.Path]::GetTempPath()) ("bp-publish-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $stash | Out-Null
$siteFarm = Join-Path $Out "wwwroot\farm.json"
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

# 放回现场的 farm.json 和 .ucf
if (Test-Path (Join-Path $stash "farm.json")) {
    Copy-Item (Join-Path $stash "farm.json") $siteFarm -Force
    Write-Host "已保留现场的 wwwroot\farm.json（仓库那份只是初始模板）"
}
Get-ChildItem $stash -Filter *.ucf | ForEach-Object { if (-not (Test-Path (Join-Path $Out $_.Name))) { Copy-Item $_.FullName $Out } }
Remove-Item $stash -Recurse -Force -ErrorAction SilentlyContinue

if ($restartService) {
    Write-Host "重新启动大屏服务……"
    sc.exe config BoltPreloadMonitor start= auto | Out-Null
    Start-Service BoltPreloadMonitor
}

Write-Host ""
Write-Host "发布完成：$Out"
Get-ChildItem $Out | ForEach-Object { "  {0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host ""
Write-Host "现场：整个文件夹拷到大屏电脑，右键「安装大屏服务.cmd」以管理员身份运行一次——开机自起、没有窗口、崩了自动重启。"
Write-Host "      调试/置零/配置用 FxMonitor.exe，「打开大屏」按钮开网页。"
