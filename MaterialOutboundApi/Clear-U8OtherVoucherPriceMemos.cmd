@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deployment\Clear-U8OtherVoucherPriceMemos.ps1" %*
set "scriptExitCode=%errorlevel%"
pause
exit /b %scriptExitCode%
