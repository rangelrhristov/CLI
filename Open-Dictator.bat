@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Dictator.ps1"
if errorlevel 1 pause & exit /b 1
start "" "%~dp0.dictator-venv\Scripts\pythonw.exe" "%~dp0dictator\fd_dictator.py"
