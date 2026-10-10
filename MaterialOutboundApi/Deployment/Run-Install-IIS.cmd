@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\MaterialOutboundApi\Deployment\Install-IIS.ps1" > "D:\MaterialOutboundApi\App_Data\install-iis.log" 2>&1
exit /b %errorlevel%
