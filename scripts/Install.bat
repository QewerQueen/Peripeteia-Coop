@echo off
rem One-click launcher for the Peripeteia Coop installer.
rem Double-click this file. It runs install.ps1 next to it.
chcp 65001 >nul
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo The installer exited with code %RC%.
  pause
)
endlocal
