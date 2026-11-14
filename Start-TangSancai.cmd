@echo off
setlocal
set "mode=%~1"
if not defined mode set "mode=auto"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-TangSancai.ps1" -Device "%mode%"
if errorlevel 1 pause
