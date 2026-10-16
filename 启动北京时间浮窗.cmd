@echo off
setlocal
rem Launch the overlay. Order: local publish folder, project root, Release build.
rem The publish folder has a non-ASCII name, so it is found at run time by
rem looking for the exe in direct subfolders instead of naming it here.
for /d %%D in ("%~dp0*") do (
    if exist "%%~fD\BeijingTimeOverlay.exe" (
        start "" "%%~fD\BeijingTimeOverlay.exe"
        exit /b 0
    )
)

if exist "%~dp0BeijingTimeOverlay.exe" (
    start "" "%~dp0BeijingTimeOverlay.exe"
    exit /b 0
)

if exist "%~dp0bin\Release\net8.0-windows\BeijingTimeOverlay.exe" (
    start "" "%~dp0bin\Release\net8.0-windows\BeijingTimeOverlay.exe"
    exit /b 0
)

echo BeijingTimeOverlay.exe was not found.
echo Run the local publish script in this folder, or download a GitHub Release package.
pause
exit /b 1
