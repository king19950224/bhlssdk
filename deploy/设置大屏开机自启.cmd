@echo off
chcp 65001 >nul
rem 没有管理员权限、装不了服务时的替代办法：登记两个计划任务（当前用户）——
rem   1) 登录时启动大屏（最小化的控制台窗口）并打开网页
rem   2) 每 5 分钟看一眼，服务不在了就重新拉起来（窗口被误关、程序意外退出都能自己恢复）
rem 有管理员权限请用「安装大屏服务.cmd」，那才是没有窗口的正规做法。
rem 取消：schtasks /Delete /TN "螺栓预紧力大屏" /F  与  schtasks /Delete /TN "螺栓预紧力大屏-守护" /F
sc query BoltPreloadMonitor >nul 2>&1
if %errorlevel%==0 (
  echo 大屏已经安装为 Windows 服务，不需要再设开机自启。
  pause
  exit /b 0
)
schtasks /Create /F /SC ONLOGON /TN "螺栓预紧力大屏" /TR "\"%~dp0启动大屏.cmd\"" /RL LIMITED
if %errorlevel% neq 0 goto :fail
schtasks /Create /F /SC MINUTE /MO 5 /TN "螺栓预紧力大屏-守护" /TR "\"%~dp0启动大屏.cmd\" nobrowser" /RL LIMITED
if %errorlevel% neq 0 goto :fail
echo.
echo 已登记：登录时启动大屏；每 5 分钟检查一次，不在就拉起。
echo 现在就启动一次？
choice /C YN /M "启动大屏"
if %errorlevel%==1 call "%~dp0启动大屏.cmd"
exit /b 0

:fail
echo 登记失败。
pause
exit /b 1
