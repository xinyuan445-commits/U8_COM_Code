@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deployment\Repair-U8OtherVoucherAmounts.ps1" %*
set "scriptExitCode=%errorlevel%"
echo.
pause
exit /b %scriptExitCode%
