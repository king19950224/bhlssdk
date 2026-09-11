@echo off
chcp 65001 >nul
rem 启动业主大屏服务（最小化的控制台窗口，别关它；关了网页就没数据了）。已经在跑就不再起第二个。
rem 带参数 nobrowser 只保证服务在跑、不开网页（开机自启和守护任务用）。
tasklist /FI "IMAGENAME eq BoltPreload.Monitor.exe" 2>nul | find /I "BoltPreload.Monitor.exe" >nul
if %errorlevel%==0 (
  if /I "%~1"=="nobrowser" exit /b 0
  echo 大屏服务已经在运行。
  start "" http://localhost:5080/
  exit /b 0
)
start "螺栓预紧力大屏" /min "%~dp0BoltPreload.Monitor.exe"
if /I "%~1"=="nobrowser" exit /b 0
timeout /t 6 >nul
start "" http://localhost:5080/
