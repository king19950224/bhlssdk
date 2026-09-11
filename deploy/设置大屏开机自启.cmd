@echo off
chcp 65001 >nul
rem 在这台电脑上登记两个计划任务（当前用户）：
rem   1) 登录时启动大屏并打开网页
rem   2) 每 5 分钟看一眼，服务不在了就重新拉起来（有人误关了窗口、程序意外退出都能自己恢复）
rem 再运行一次会覆盖旧任务（换了目录也没关系）。
rem 取消：schtasks /Delete /TN "螺栓预紧力大屏" /F  与  schtasks /Delete /TN "螺栓预紧力大屏-守护" /F
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
echo 登记失败。请右键"以管理员身份运行"再试。
pause
exit /b 1
