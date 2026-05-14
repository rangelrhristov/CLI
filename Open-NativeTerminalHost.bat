@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-NativeTerminalHost.ps1"
if errorlevel 1 pause & exit /b 1
start "" "%~dp0CLI.exe"
