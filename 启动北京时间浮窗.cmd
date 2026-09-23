@echo off
setlocal
if exist "%~dp0bin\Release\net8.0-windows\BeijingTimeOverlay.exe" (
    start "" "%~dp0bin\Release\net8.0-windows\BeijingTimeOverlay.exe"
    exit /b 0
)

if exist "%~dp0BeijingTimeOverlay.exe" (
    start "" "%~dp0BeijingTimeOverlay.exe"
    exit /b 0
)

if exist "%~dp0发布\BeijingTimeOverlay.exe" (
    start "" "%~dp0发布\BeijingTimeOverlay.exe"
    exit /b 0
)

echo 没有找到可运行程序。请下载 GitHub Release 程序包，或先构建项目。
pause
exit /b 1
exit /b 0
