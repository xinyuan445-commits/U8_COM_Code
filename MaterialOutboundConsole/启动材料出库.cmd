@echo off
chcp 65001 >nul
title 用友U8材料出库单

pushd "%~dp0bin\Release\net48"
MaterialOutboundConsole.exe select-add

echo.
echo 程序已结束，请查看上方执行结果。
pause
popd
