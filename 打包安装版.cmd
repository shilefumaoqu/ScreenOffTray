@echo off
chcp 65001 >nul
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Installer\build.ps1"
set "SCREENOFF_BUILD_EXIT=%ERRORLEVEL%"
if not "%SCREENOFF_BUILD_EXIT%"=="0" echo Build failed. See the build log.
pause
exit /b %SCREENOFF_BUILD_EXIT%
