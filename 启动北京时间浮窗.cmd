@echo off
setlocal
chcp 65001 >nul
rem 本文件以 UTF-8 保存，先切换代码页，后面的中文路径和提示才能被正确读取。
if exist "%~dp0发布\BeijingTimeOverlay.exe" (
    start "" "%~dp0发布\BeijingTimeOverlay.exe"
    exit /b 0
)

if exist "%~dp0BeijingTimeOverlay.exe" (
    start "" "%~dp0BeijingTimeOverlay.exe"
    exit /b 0
)

if exist "%~dp0bin\Release\net8.0-windows\BeijingTimeOverlay.exe" (
    start "" "%~dp0bin\Release\net8.0-windows\BeijingTimeOverlay.exe"
    exit /b 0
)

echo 没有找到可运行程序。请双击“发布到本地.cmd”，或下载 GitHub Release 程序包。
pause
exit /b 1
