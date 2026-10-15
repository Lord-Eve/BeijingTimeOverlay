@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-local.ps1" %*
set "result=%ERRORLEVEL%"
echo.
pause
exit /b %result%
