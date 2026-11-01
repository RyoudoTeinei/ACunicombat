@echo off
chcp 65001 >nul
call "%~dp0启动开发资料库.cmd" --no-open
if errorlevel 1 exit /b 1
start "" "http://127.0.0.1:5187/assets.html"
