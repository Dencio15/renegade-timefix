@echo off
setlocal
title TimeFix - sync clock and time zone now

rem Already elevated? "net session" only succeeds with admin rights.
net session >nul 2>&1
if %errorlevel%==0 goto RUN

echo Requesting administrator rights...
echo (Setting the CLOCK requires admin. The TIME ZONE alone does not.)
echo.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
if errorlevel 1 goto NOADMIN
exit /b 0

:NOADMIN
echo.
echo Could not elevate. Setting the time zone only - the clock will NOT be corrected.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sync-Time.ps1"
echo.
pause
exit /b 1

:RUN
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sync-Time.ps1"
echo.
pause
exit /b %errorlevel%
