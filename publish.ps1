# 一键发布：桌面端 + 大屏 + 页面 + farm.json + 标定文件，全部放进一个文件夹，拷走就能用。
#   .\publish.ps1            → dist\螺栓预紧力监测\
#   .\publish.ps1 -Out D:\发布\xxx
# 两个 exe 都是自包含单文件（目标机不用装 .NET）。同一个文件夹里两边共用 wwwroot\farm.json 和 .ucf：
# 桌面端找不到自己旁边的 farm.json 时会读 wwwroot\farm.json，现场配置对话框也只写这一份。
param([string]$Out = (Join-Path $PSScriptRoot "dist\螺栓预紧力监测"))

$ErrorActionPreference = "Stop"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }

# 这台机器上如果大屏服务正跑着发布目录里的 exe，先停下来（文件锁着删不掉），发完再起
$svc = Get-Service BoltPreloadMonitor -ErrorAction SilentlyContinue
$restartService = $false
if ($svc -and $svc.Status -eq "Running") {
    Write-Host "大屏服务在运行，先停止……"
    Stop-Service BoltPreloadMonitor -ErrorAction Stop
    $restartService = $true
}

if (Test-Path $Out) {
    # 历史库和日志是现场数据，不随发布清掉
    Get-ChildItem $Out -Force | Where-Object { $_.Name -notin @("data", "logs") } | Remove-Item -Recurse -Force
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
Get-ChildItem $Out -Include *.pdb, settings.json, appsettings*.json, web.config, *.staticwebassets.endpoints.json -Recurse -Depth 0 |
    Remove-Item -Force -ErrorAction SilentlyContinue

# 业主机上装/卸 Windows 服务的两个脚本
foreach ($f in "安装大屏服务.cmd", "卸载大屏服务.cmd") {
    Copy-Item (Join-Path $PSScriptRoot "deploy\$f") $Out
}

if ($restartService) {
    Write-Host "重新启动大屏服务……"
    Start-Service BoltPreloadMonitor
}

Write-Host ""
Write-Host "发布完成：$Out"
Get-ChildItem $Out | ForEach-Object { "  {0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host ""
Write-Host "现场：整个文件夹拷到大屏电脑，右键「安装大屏服务.cmd」以管理员身份运行一次——开机自起、没有窗口、崩了自动重启。"
Write-Host "      调试/置零/配置用 FxMonitor.exe，「打开大屏」按钮开网页。"
