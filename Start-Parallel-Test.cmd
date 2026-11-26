@echo off
setlocal
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0scripts\Test-ParallelDesktop.ps1" %*
set "CU_PARALLEL_EXIT=%ERRORLEVEL%"
echo.
echo Parallel probe exit code: %CU_PARALLEL_EXIT%
pause
exit /b %CU_PARALLEL_EXIT%
