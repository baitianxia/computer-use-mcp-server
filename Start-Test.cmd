@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Test-Windows.ps1"
set "CU_TEST_EXIT=%ERRORLEVEL%"
echo.
echo Test runner exit code: %CU_TEST_EXIT%
pause
exit /b %CU_TEST_EXIT%
