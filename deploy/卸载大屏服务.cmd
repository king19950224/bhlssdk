@echo off
chcp 65001 >nul
rem 停止并删除大屏 Windows 服务、防火墙规则。程序文件、历史库（data\）、日志（logs\）都不动。需要管理员权限。
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo 请右键这个文件，选“以管理员身份运行”。
  pause
  exit /b 1
)
sc query BoltPreloadMonitor >nul 2>&1
if %errorlevel% neq 0 (
  echo 没有安装大屏服务。
  pause
  exit /b 0
)
sc stop BoltPreloadMonitor >nul 2>&1
ping -n 4 127.0.0.1 >nul
sc delete BoltPreloadMonitor
netsh advfirewall firewall delete rule name="螺栓预紧力大屏" >nul 2>&1
echo 已卸载。历史数据在 %~dp0data\，日志在 %~dp0logs\，没有删。
pause
