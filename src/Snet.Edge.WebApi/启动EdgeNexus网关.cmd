@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-EdgeNexus.ps1"
if errorlevel 1 (
  echo.
  echo EdgeNexus startup failed. Check the data\logs directory.
  pause
)
