<#
.SYNOPSIS
    HTTPS 定稿一键配置：自签服务器证书 + 本机受信根 + Kestrel 配置 + 重启服务。

.DESCRIPTION
    部署形态是集控室/塔基工控机、纯局域网，公网 CA 证书拿不到。定稿方案是单机一张
    自签服务器证书直接进受信任根——不养"自签 CA + 服务器证书"那套小 PKI：单机部署多一层
    CA 只多一个可弄丢的私钥，客户有自己的 PKI 时直接换 pfx，两头都用不上中间层。

    脚本做的事，按顺序：
      1) 生成带 SAN（主机名/localhost/可选 IP）的服务器证书，默认 5 年，落 LocalMachine\My；
      2) 清掉本脚本此前生成的旧证书（My 与 Root 两个店，按 FriendlyName/Subject 匹配），
         避免反复执行后证书店里攒一堆过期同名证书；
      3) 导出 pfx（Kestrel 用）与 .cer（发给其它工作站装受信根用）到安装目录 certs\；
      4) 证书导入本机"受信任的根证书颁发机构"——工控机自己的浏览器与健康检查直接信任；
      5) 往 appsettings.Production.json 写 Kestrel 节：对外 HTTPS + 仅 127.0.0.1 的 HTTP 诊断口
         （装完自检清单、collect-diagnostics 继续走本机 HTTP 口，不受影响）；
      6) 重启服务并走 HTTPS 探健康检查——探测不跳证书校验，跳了等于没验证受信根这步。

    Kestrel 节生效后，服务环境变量里的 ASPNETCORE_URLS 会被覆盖，启动日志有一行
    "Overriding address(es)" 提示，属预期。

    pfx 口令写在 appsettings.Production.json：口令保护的是静态文件，而配置文件与 pfx 本就
    同目录同 ACL，挪去环境变量不改变信任边界；collect-diagnostics.ps1 打包时会按脱敏正则抹掉。

    客户自有 PKI 的场合：拿他们签的 pfx 替换 certs\ 下的文件、改 Kestrel 节的 Path/Password
    即可，不用重跑本脚本、不改代码。

.EXAMPLE
    .\setup-https.ps1 -OpenFirewall
    .\setup-https.ps1 -IpAddresses 192.168.1.10 -OpenFirewall   # 操作员按 IP 访问时把 IP 编进证书
#>
param(
    [string]$InstallDir = "C:\BoltPreload",
    [int]$HttpsPort = 8443,
    [int]$LocalHttpPort = 5000,
    [string[]]$DnsNames = @($env:COMPUTERNAME, "localhost"),
    [string[]]$IpAddresses = @(),
    [int]$ValidYears = 5,
    [string]$PfxPassword = "",
    [string]$ServiceName = "BoltPreload.Api",
    [switch]$OpenFirewall,
    [switch]$NoServiceRestart
)

$ErrorActionPreference = "Stop"

$certDir = Join-Path $InstallDir "certs"
if (!(Test-Path $certDir)) {
    New-Item -ItemType Directory -Path $certDir | Out-Null
}

if ($PfxPassword -eq "") {
    $bytes = New-Object byte[] 16
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $PfxPassword = [Convert]::ToBase64String($bytes)
}

# ---------------------------------------------------------------- 生成证书

# SAN 把访问会用到的名字都编进去（主机名/localhost/IP），少一个浏览器就告警一个；
# EKU 限定服务器身份验证（1.3.6.1.5.5.7.3.1）
$sanParts = @($DnsNames | ForEach-Object { "DNS=$_" }) + @($IpAddresses | ForEach-Object { "IPAddress=$_" })
$san = "2.5.29.17={text}" + ($sanParts -join "&")
$subject = "CN=$($DnsNames[0])"

$cert = New-SelfSignedCertificate `
    -Subject $subject `
    -FriendlyName "BoltPreload HTTPS" `
    -TextExtension @($san, "2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\LocalMachine\My

# 清掉此前跑本脚本留下的旧证书（Root 店里 FriendlyName 不随 .cer 导入保留，所以 Subject 也要匹配）
Get-ChildItem Cert:\LocalMachine\My, Cert:\LocalMachine\Root |
    Where-Object { ($_.FriendlyName -eq "BoltPreload HTTPS" -or $_.Subject -eq $subject) -and $_.Thumbprint -ne $cert.Thumbprint } |
    Remove-Item

$pfxPath = Join-Path $certDir "boltpreload-https.pfx"
$cerPath = Join-Path $certDir "boltpreload-https.cer"
$securePwd = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Write-Host "证书已生成并进本机受信任根：$subject（SAN: $($sanParts -join ', ')，有效期 $ValidYears 年）"

# ---------------------------------------------------------------- 写 Kestrel 配置

# 与 install-service.ps1 生成的是同一份文件：读回来补 Kestrel 节，不动其余配置
$configPath = Join-Path $InstallDir "appsettings.Production.json"
if (Test-Path $configPath) {
    $config = Get-Content -Path $configPath -Raw | ConvertFrom-Json
} else {
    Write-Warning "未发现 $configPath（通常由 install-service.ps1 生成，两个脚本先后顺序不限）。新建仅含 Kestrel 节的配置。"
    $config = [pscustomobject]@{
        "//" = "本文件由 deploy/windows 脚本生成，仅存放非敏感的现场配置；密钥走服务级环境变量。"
    }
}

$kestrel = [ordered]@{
    "//"      = "由 setup-https.ps1 生成：对外只开 HTTPS；127.0.0.1 的 HTTP 口留给本机诊断（自检清单/诊断脚本）。换客户 PKI 证书 = 替换 pfx 并改 Path/Password。"
    Endpoints = [ordered]@{
        Https     = [ordered]@{ Url = "https://0.0.0.0:$HttpsPort" }
        LocalHttp = [ordered]@{ Url = "http://127.0.0.1:$LocalHttpPort" }
    }
    Certificates = [ordered]@{
        Default = [ordered]@{ Path = $pfxPath; Password = $PfxPassword }
    }
}
$config | Add-Member -MemberType NoteProperty -Name Kestrel -Value $kestrel -Force
$config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
Write-Host "Kestrel 配置已写入：$configPath（对外 https://0.0.0.0:$HttpsPort，本机诊断 http://127.0.0.1:$LocalHttpPort）"

# ---------------------------------------------------------------- 防火墙与重启

if ($OpenFirewall) {
    New-NetFirewallRule `
        -DisplayName "BoltPreload HTTPS $HttpsPort" `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort `
        -ErrorAction SilentlyContinue | Out-Null
    Write-Host "已开放入站端口 $HttpsPort"
} else {
    Write-Host "未开放防火墙端口（局域网访问请加 -OpenFirewall）"
}

if (-not $NoServiceRestart -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Restart-Service -Name $ServiceName
    $healthUrl = "https://localhost:$HttpsPort/api/health/ready"
    Write-Host "等待服务就绪（走 HTTPS 验证证书链）：$healthUrl"

    $ready = $false
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 2
        try {
            $resp = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5
            if ($resp.status -eq "ready") { $ready = $true; break }
        } catch { }
    }
    if ($ready) {
        Write-Host "HTTPS 就绪检查通过。局域网访问地址：https://$($DnsNames[0]):$HttpsPort"
    } else {
        Write-Warning "服务未在 60s 内经 HTTPS 就绪。查 $InstallDir\logs\：若是证书报错，确认 pfx 路径与口令；若是端口占用，换 -HttpsPort。"
    }
} else {
    Write-Host "未重启服务（服务不存在或指定了 -NoServiceRestart），配置将在下次启动生效。"
}

Write-Host ""
Write-Host "其它工作站要免告警访问：把 $cerPath 装进各自的'受信任的根证书颁发机构'（域环境可用组策略统一推）。"
