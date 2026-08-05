param(
    [string]$InstallDir = "C:\BoltPreload",
    [string]$OutputDir = "C:\BoltPreload\diagnostics"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$bundle = Join-Path $OutputDir "boltpreload-diagnostics-$timestamp"
New-Item -ItemType Directory -Path $bundle | Out-Null

Get-Service | Where-Object { $_.Name -like "*BoltPreload*" } |
    Out-File (Join-Path $bundle "services.txt")

Get-Process | Sort-Object CPU -Descending |
    Select-Object -First 50 |
    Out-File (Join-Path $bundle "processes.txt")

netstat -ano | Out-File (Join-Path $bundle "netstat.txt")

if (Test-Path (Join-Path $InstallDir "logs")) {
    Copy-Item (Join-Path $InstallDir "logs") (Join-Path $bundle "logs") -Recurse -Force
}

# 现场真配置在 appsettings.Production.json（install-service.ps1 写的就是它），只收 appsettings.json 等于没收到配置。
# 诊断包可能外发，拷贝时把连接串密码和 Jwt Key 抹掉。
foreach ($cfg in "appsettings.json", "appsettings.Production.json") {
    $src = Join-Path $InstallDir $cfg
    if (Test-Path $src) {
        (Get-Content $src -Raw) `
            -replace '(?i)(Password|Pwd)=[^;"]*', '$1=***' `
            -replace '(?i)("(?:Key|Password)"\s*:\s*")[^"]*(")', '$1***$2' |
            Out-File (Join-Path $bundle $cfg)
    }
}

Compress-Archive -Path "$bundle\*" -DestinationPath "$bundle.zip" -Force
Write-Host "Diagnostics bundle: $bundle.zip"
