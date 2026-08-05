<#
.SYNOPSIS
    注册每日数据库备份计划任务（SYSTEM 账户，默认 02:00，滚动保留 14 份）。

.DESCRIPTION
    backup-postgres.ps1 一直能手工跑，缺的只是没人定时跑——没有计划任务的备份等于没有备份。
    本脚本注册每日任务，执行体就是同目录的 backup-postgres.ps1。

    口令两条路：优先给 SYSTEM 账户配 pgpass.conf（路径见 deployment-guide §7.1）；
    配不了的环境用 -PgPassword 直接带参——参数会写进任务定义（本机管理员可读），
    信任边界与 NSSM 服务环境变量相同。

.EXAMPLE
    .\register-backup-task.ps1 -PgPassword "<db 口令>" -RunNow   # 注册并当场跑一次验证
    .\register-backup-task.ps1 -At "03:30"                       # 换执行时间（重跑即覆盖）
    .\register-backup-task.ps1 -Unregister                       # 移除任务
#>
param(
    [string]$TaskName = "BoltPreload-DailyBackup",
    [string]$At = "02:00",
    [string]$BackupScript = (Join-Path $PSScriptRoot "backup-postgres.ps1"),
    [string]$PgDump = "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
    [string]$Database = "boltpreload",
    [string]$User = "boltpreload",
    [string]$DbHost = "127.0.0.1",
    [string]$BackupDir = "C:\BoltPreload\backups",
    [int]$KeepCount = 14,
    [string]$PgPassword = "",
    [switch]$RunNow,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "已移除计划任务 $TaskName"
    return
}

if (!(Test-Path $BackupScript)) {
    throw "找不到备份脚本：$BackupScript（本脚本默认与 backup-postgres.ps1 同目录）"
}

$taskArgs = @(
    "-NoProfile", "-ExecutionPolicy", "Bypass",
    "-File", "`"$BackupScript`"",
    "-PgDump", "`"$PgDump`"",
    "-Database", $Database,
    "-User", $User,
    "-HostName", $DbHost,
    "-BackupDir", "`"$BackupDir`"",
    "-KeepCount", $KeepCount
)
if ($PgPassword -ne "") {
    $taskArgs += @("-PgPassword", "`"$PgPassword`"")
}

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument ($taskArgs -join ' ')
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
# StartWhenAvailable：02:00 恰逢停机（检修断电）则开机后补跑，不整天空窗
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)

# -Force：已存在即覆盖，改时间/参数直接重跑本脚本
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "已注册计划任务 $TaskName：每日 $At 备份 $Database → $BackupDir（保留最近 $KeepCount 份）"

if ($RunNow) {
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "已触发立即执行，等待完成..."
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 2
        if ((Get-ScheduledTask -TaskName $TaskName).State -ne "Running") { break }
    }
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    if ($info.LastTaskResult -eq 0) {
        $latest = Get-ChildItem -Path $BackupDir -Filter "$Database-*.dump" |
            Sort-Object Name -Descending | Select-Object -First 1
        Write-Host "本次备份成功：$($latest.FullName)（$([math]::Round($latest.Length / 1MB, 1)) MB）"
    } else {
        Write-Warning ("备份未成功（LastTaskResult=$($info.LastTaskResult)）。" +
            "常见原因：口令没给到（pgpass.conf 未配给 SYSTEM / 未带 -PgPassword）、pg_dump 路径不对。" +
            "可手工执行 $BackupScript 复现排查。")
    }
}
