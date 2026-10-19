@echo off
chcp 65001 >nul
rem 把大屏装成 Windows 服务（推荐）：开机自起、不用登录、没有窗口，意外退出由系统 5 秒后自动重启。
rem 需要管理员权限。再运行一次 = 重装（换了目录也没关系）。卸载用「卸载大屏服务.cmd」。
rem 桌面端「打开大屏」会带 nobrowser 参数、stdin 接 nul 静默调用它：不开浏览器，pause 不停，退出码 0 才算装好。
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

rem 已装过的：先停、等它真停下来（进程退完），再删、等服务项真消失。
rem 以前固定等 3 秒就删、建、起：旧进程还没退，新实例抢不到 5080 端口，服务刚装好就"意外终止"一次再被拉起
sc query BoltPreloadMonitor >nul 2>&1
if %errorlevel%==0 (
  echo 停止已有的大屏服务……
  sc stop BoltPreloadMonitor >nul 2>&1
  call :wait_stopped
  sc delete BoltPreloadMonitor >nul 2>&1
  call :wait_deleted
)

rem 到这里服务已经不在了，还活着的 BoltPreload.Monitor.exe 只可能是双击起来的控制台实例，关掉免得抢 5080 端口。
rem （服务在跑的时候不能 taskkill：那是在杀服务进程，系统会按恢复策略 5 秒后把它拉起来，正撞上下面的安装）
taskkill /IM BoltPreload.Monitor.exe /F >nul 2>&1

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
call :wait_running
sc query BoltPreloadMonitor | find "RUNNING" >nul
if %errorlevel%==0 (
  echo.
  echo 已安装并启动。以后开机自动运行，不需要登录，也没有窗口。
  echo 本机网页：http://localhost:5080/   局域网用本机 IP 代替 localhost。
  echo 运行日志：%~dp0logs\
  if /I not "%~1"=="nobrowser" start "" http://localhost:5080/
  pause
  exit /b 0
)
echo.
echo 服务已安装但没有进入运行状态，请查看 %~dp0logs\ 里今天的日志。
pause
exit /b 2

rem ── 子过程：轮询服务/进程状态，等到位或等满时限就返回（每轮约 1 秒） ──────────────
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
rem 状态到 STOPPED 之后进程可能还在收尾（写日志、合库），再等它最多 10 秒
set /a _n=0
:wait_stopped_proc_loop
tasklist /FI "IMAGENAME eq BoltPreload.Monitor.exe" | find /I "BoltPreload.Monitor.exe" >nul
if %errorlevel% neq 0 exit /b 0
set /a _n+=1
if %_n% geq 10 exit /b 0
ping -n 2 127.0.0.1 >nul
goto :wait_stopped_proc_loop

:wait_deleted
rem sc delete 只是"标记删除"，服务项要等所有句柄关了才真消失（services.msc 开着会一直占着）；最多等 15 秒
set /a _n=0
:wait_deleted_loop
sc query BoltPreloadMonitor >nul 2>&1
if %errorlevel% neq 0 exit /b 0
set /a _n+=1
if %_n% geq 15 exit /b 0
ping -n 2 127.0.0.1 >nul
goto :wait_deleted_loop

:wait_running
rem 起来要连设备、开库，给它最多 15 秒
set /a _n=0
:wait_running_loop
sc query BoltPreloadMonitor | find "RUNNING" >nul
if %errorlevel%==0 exit /b 0
set /a _n+=1
if %_n% geq 15 exit /b 0
ping -n 2 127.0.0.1 >nul
goto :wait_running_loop
