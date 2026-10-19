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
echo 停止大屏服务……
sc stop BoltPreloadMonitor >nul 2>&1
call :wait_stopped
sc delete BoltPreloadMonitor
netsh advfirewall firewall delete rule name="螺栓预紧力大屏" >nul 2>&1
echo 已卸载。历史数据在 %~dp0data\，日志在 %~dp0logs\，没有删。
pause
exit /b 0

rem ── 等服务真停下来（状态 STOPPED 且进程退完），最多约 50 秒 ──────────────────────
:wait_stopped
set /a _n=0
:wait_stopped_loop
sc query BoltPreloadMonitor | find "STOPPED" >nul
if %errorlevel%==0 goto :wait_stopped_proc
set /a _n+=1
if %_n% geq 40 goto :wait_stopped_proc
ping -n 2 127.0.0.1 >nul
goto :wait_stopped_loop
:wait_stopped_proc
set /a _n=0
:wait_stopped_proc_loop
tasklist /FI "IMAGENAME eq BoltPreload.Monitor.exe" | find /I "BoltPreload.Monitor.exe" >nul
if %errorlevel% neq 0 exit /b 0
set /a _n+=1
if %_n% geq 10 exit /b 0
ping -n 2 127.0.0.1 >nul
goto :wait_stopped_proc_loop
