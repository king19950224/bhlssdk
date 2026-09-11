@echo off
chcp 65001 >nul
rem 把大屏装成 Windows 服务（推荐）：开机自起、不用登录、没有窗口，意外退出由系统 5 秒后自动重启。
rem 需要管理员权限。再运行一次 = 重装（换了目录也没关系）。卸载用「卸载大屏服务.cmd」。
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo 请右键这个文件，选“以管理员身份运行”。
  pause
  exit /b 1
)
set "EXE=%~dp0BoltPreload.Monitor.exe"
if not exist "%EXE%" (
  echo 找不到 %EXE%
  pause
  exit /b 1
)

rem 之前双击控制台方式跑着的先关掉，免得两份抢 5080 端口
taskkill /IM BoltPreload.Monitor.exe /F >nul 2>&1

sc query BoltPreloadMonitor >nul 2>&1
if %errorlevel%==0 (
  sc stop BoltPreloadMonitor >nul 2>&1
  ping -n 4 127.0.0.1 >nul
  sc delete BoltPreloadMonitor >nul 2>&1
  ping -n 3 127.0.0.1 >nul
)

sc create BoltPreloadMonitor binPath= "\"%EXE%\"" start= auto DisplayName= "螺栓预紧力大屏服务"
if %errorlevel% neq 0 (
  echo 创建服务失败。
  pause
  exit /b 1
)
sc description BoltPreloadMonitor "螺栓预紧力在线监测 业主大屏（BoltPreload.Monitor）。网页：http://本机IP:5080/  日志：程序目录\logs" >nul
rem 意外退出：5 秒、10 秒、30 秒后依次重启；一天没再出事就重新计数
sc failure BoltPreloadMonitor reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul
rem 局域网里别的电脑/手机要能开网页
netsh advfirewall firewall delete rule name="螺栓预紧力大屏" >nul 2>&1
netsh advfirewall firewall add rule name="螺栓预紧力大屏" dir=in action=allow protocol=TCP localport=5080 >nul

sc start BoltPreloadMonitor >nul
ping -n 7 127.0.0.1 >nul
sc query BoltPreloadMonitor | find "RUNNING" >nul
if %errorlevel%==0 (
  echo.
  echo 已安装并启动。以后开机自动运行，不需要登录，也没有窗口。
  echo 本机网页：http://localhost:5080/   局域网用本机 IP 代替 localhost。
  echo 运行日志：%~dp0logs\
  start "" http://localhost:5080/
) else (
  echo.
  echo 服务已安装但没有进入运行状态，请查看 %~dp0logs\ 里今天的日志。
)
pause
