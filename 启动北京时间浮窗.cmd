@echo off
setlocal
if exist "%~dp0BeijingTimeOverlay.exe" (
    start "" "%~dp0BeijingTimeOverlay.exe"
    exit /b 0
)

if exist "%~dp0发布\BeijingTimeOverlay.exe" (
    start "" "%~dp0发布\BeijingTimeOverlay.exe"
    exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo 未找到 .NET SDK。请先安装 .NET 8 SDK，或先发布应用后再运行。
    pause
    exit /b 1
)

dotnet run --project "%~dp0北京时间浮窗.csproj" -c Release
exit /b %errorlevel%
exit /b 0
