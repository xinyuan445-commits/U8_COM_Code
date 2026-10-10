@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deployment\Update-U8OtherVoucherPrices.ps1" %*
set "scriptExitCode=%errorlevel%"
exit /b %scriptExitCode%
