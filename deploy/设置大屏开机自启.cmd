@echo off
chcp 65001 >nul
rem 在这台电脑上登记一个"登录时启动大屏"的计划任务（当前用户）。业主电脑重启后大屏自己起来，不用人管。
rem 再运行一次会覆盖旧任务（换了目录也没关系）。取消：schtasks /Delete /TN "螺栓预紧力大屏" /F
schtasks /Create /F /SC ONLOGON /TN "螺栓预紧力大屏" /TR "\"%~dp0启动大屏.cmd\"" /RL LIMITED
if %errorlevel% neq 0 (
  echo 登记失败。请右键"以管理员身份运行"再试。
  pause
  exit /b 1
)
echo.
echo 已登记：每次登录自动运行 %~dp0启动大屏.cmd
echo 现在就启动一次？
choice /C YN /M "启动大屏"
if %errorlevel%==1 call "%~dp0启动大屏.cmd"
