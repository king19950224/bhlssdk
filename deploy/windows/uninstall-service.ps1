<#
.SYNOPSIS
    卸载 BoltPreload 服务与随装痕迹（计划任务 / 防火墙规则）。

.DESCRIPTION
    数据一概不动：数据库、data\（WAL/波形/报告）、logs\、自签证书（已进受信根，删除影响
    其他引用方，交接单里写明手工清理方式）全部保留——卸载不许顺手删监测数据。
    -KeepScheduledTask / -KeepFirewallRules 供"重装前先卸载"的场景保留周边配置。
#>
param(
    [string]$ServiceName = "BoltPreload.Api",
    [string]$NssmPath = "C:\BoltPreload\tools\nssm.exe",
    [switch]$KeepScheduledTask,
    [switch]$KeepFirewallRules
)

$ErrorActionPreference = "Stop"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    if (Test-Path $NssmPath) {
        & $NssmPath stop $ServiceName | Out-Null
        & $NssmPath remove $ServiceName confirm | Out-Null
    } else {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
    }
    Write-Host "Removed service $ServiceName"
} else {
    Write-Host "Service not found: $ServiceName"
}

# 每日备份计划任务（register-backup-task.ps1 注册的）：留着的话卸载后仍每天以 SYSTEM 跑 pg_dump
if (-not $KeepScheduledTask) {
    if (Get-ScheduledTask -TaskName "BoltPreload-DailyBackup" -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName "BoltPreload-DailyBackup" -Confirm:$false
        Write-Host "Removed scheduled task BoltPreload-DailyBackup"
    }
}

# 防火墙规则（install-service -OpenFirewall / setup-https -OpenFirewall 开的）：按 DisplayName 前缀清
if (-not $KeepFirewallRules) {
    $rules = Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "BoltPreload API *" -or $_.DisplayName -like "BoltPreload HTTPS *" }
    foreach ($r in $rules) {
        Remove-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue
        Write-Host "Removed firewall rule '$($r.DisplayName)'"
    }
}

Write-Host "数据未动：数据库、data\、logs\ 与受信根里的自签证书均保留（清理方式见 deployment-guide §9）"
