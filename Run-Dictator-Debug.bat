@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Dictator.ps1"
if errorlevel 1 pause & exit /b 1
"%~dp0.dictator-venv\Scripts\python.exe" "%~dp0dictator\fd_dictator.py"
pause
