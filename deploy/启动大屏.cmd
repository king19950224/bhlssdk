@echo off
chcp 65001 >nul
rem 以控制台方式启动大屏（最小化窗口，别关它；关了网页就没数据了）。已经在跑就不再起第二个。
rem 带参数 nobrowser 只保证服务在跑、不开网页（开机自启和守护任务用）。
rem 装成了 Windows 服务的机器上不用这个：服务自己会跑，这里只开网页。
sc query BoltPreloadMonitor >nul 2>&1
if %errorlevel%==0 (
  if /I "%~1"=="nobrowser" exit /b 0
  start "" http://localhost:5080/
  exit /b 0
)
tasklist /FI "IMAGENAME eq BoltPreload.Monitor.exe" 2>nul | find /I "BoltPreload.Monitor.exe" >nul
if %errorlevel%==0 (
  if /I "%~1"=="nobrowser" exit /b 0
  echo 大屏服务已经在运行。
  start "" http://localhost:5080/
  exit /b 0
)
start "螺栓预紧力大屏" /min "%~dp0BoltPreload.Monitor.exe"
if /I "%~1"=="nobrowser" exit /b 0
ping -n 7 127.0.0.1 >nul
start "" http://localhost:5080/
