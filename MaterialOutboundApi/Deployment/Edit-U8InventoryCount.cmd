@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Edit-U8InventoryCount.ps1"
echo.
pause
