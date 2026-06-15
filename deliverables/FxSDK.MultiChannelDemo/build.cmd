@echo off
setlocal
cd /d "%~dp0"

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

dotnet restore FxSDK.MultiChannelDemo.sln || exit /b 1
dotnet build FxSDK.MultiChannelDemo.sln -c %CONFIG% --no-restore || exit /b 1
