<#
.SYNOPSIS
    组装 Inno Setup 的 payload\ 目录并（可选）编译出一键安装包。

.DESCRIPTION
    按同目录 README.md 的布局摆料：
      payload\app\            自包含发布（免装 .NET；wwwroot 由 csproj 从 web/dist 打入）
      payload\deploy\sql\     全部迁移
      payload\deploy\windows\ 全部运维脚本（含 install-all.ps1）
      payload\docs\           现场手册（deployment-guide / troubleshooting）
      payload\nssm.exe        需预先放置（nssm.cc 2.24 win64；本脚本不自动下载，出包机可能离线）
      payload\license-public.pem  由 -LicensePublicKey 指定——公钥决定现场认哪把私钥签的授权，
                              绝不能默认生成，出错等于把整套授权体系换掉

    前端产物：脚本先跑 npm run build 保证 web/dist 是当次代码（publish 只负责拷贝，不负责新鲜度）。

.EXAMPLE
    .\build-payload.ps1 -LicensePublicKey C:\keys\license-public.pem
    .\build-payload.ps1 -LicensePublicKey C:\keys\license-public.pem -Compile
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$LicensePublicKey,
    [switch]$Compile,
    [switch]$SkipWebBuild
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$payload = Join-Path $PSScriptRoot "payload"

if (-not (Test-Path $LicensePublicKey)) { throw "授权公钥不存在：$LicensePublicKey" }
$nssm = Join-Path $payload "nssm.exe"
if (-not (Test-Path $nssm)) {
    throw "缺 payload\nssm.exe：从 nssm.cc 下载 2.24 解出 win64\nssm.exe 放到 $payload"
}

# 1) 前端构建（vue-tsc + vite；类型错误在这里就报，不带病出包）
if (-not $SkipWebBuild) {
    Write-Host "[payload] npm run build（web/dist）"
    Push-Location (Join-Path $root "web")
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "前端构建失败" }
    }
    finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $root "web\dist\index.html"))) { throw "web/dist 缺 index.html，交付包会没有界面" }

# 2) 自包含发布（win-x64，免装 .NET 运行时）
$app = Join-Path $payload "app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
Write-Host "[payload] dotnet publish（self-contained win-x64）"
dotnet publish (Join-Path $root "src\BoltPreload.Api\BoltPreload.Api.csproj") `
    -c Release -r win-x64 --self-contained true -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
if (-not (Test-Path (Join-Path $app "BoltPreload.Api.exe"))) { throw "publish 没产出 BoltPreload.Api.exe" }
if (-not (Test-Path (Join-Path $app "wwwroot\index.html"))) { throw "publish 没把 web/dist 打进 wwwroot" }
# 开发配置绝不出厂（csproj 已设 CopyToPublishDirectory=Never，这里反向自检一道）
if (Test-Path (Join-Path $app "appsettings.Development.json")) { throw "publish 产物混入 appsettings.Development.json" }

# 3) 迁移与运维脚本
$dep = Join-Path $payload "deploy"
if (Test-Path $dep) { Remove-Item $dep -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $dep "sql"), (Join-Path $dep "windows") | Out-Null
Copy-Item (Join-Path $root "deploy\sql\0*.sql") (Join-Path $dep "sql") -Force
Copy-Item (Join-Path $root "deploy\windows\*.ps1") (Join-Path $dep "windows") -Force
# dev 种子与 maintenance 手工脚本不出厂：生产机跑了 dev 种子就是带着演示数据交付
if (Test-Path (Join-Path $dep "sql\dev")) { throw "payload 混入 dev 种子" }

# 4) 现场手册
$docs = Join-Path $payload "docs"
if (Test-Path $docs) { Remove-Item $docs -Recurse -Force }
New-Item -ItemType Directory -Force -Path $docs | Out-Null
foreach ($f in @("deployment-guide.md", "troubleshooting.md")) {
    $srcDoc = Join-Path $root "资料\工程文档\$f"
    if (Test-Path $srcDoc) { Copy-Item $srcDoc $docs -Force }
    else { Write-Warning "手册缺失：$f（照常出包，交付前补齐）" }
}

# 5) 授权公钥
Copy-Item $LicensePublicKey (Join-Path $payload "license-public.pem") -Force

Write-Host "[payload] 就绪：$payload"
Get-ChildItem $payload | ForEach-Object { Write-Host ("  " + $_.Name) }

# 6) 可选：编译安装包（ISCC 位置按装法不同：管理员装在 Program Files (x86)，winget 用户级装在 LOCALAPPDATA）
if ($Compile) {
    $iscc = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "未找到 ISCC.exe，请先安装 Inno Setup 6" }
    Write-Host "[payload] ISCC 编译 installer.iss"
    & $iscc (Join-Path $PSScriptRoot "installer.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }
    Get-ChildItem (Join-Path $PSScriptRoot "output") | ForEach-Object {
        Write-Host ("[payload] 产物：" + $_.FullName + "（" + [math]::Round($_.Length / 1MB, 1) + " MB）")
    }
}
