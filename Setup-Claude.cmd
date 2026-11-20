@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Claude.ps1"
set "CU_SETUP_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %CU_SETUP_EXIT%
