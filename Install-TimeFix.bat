@echo off
setlocal
title TimeFix - one-time install

net session >nul 2>&1
if %errorlevel%==0 goto RUN

echo This installer needs administrator rights (it registers a SYSTEM task).
echo.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
if errorlevel 1 goto NOADMIN
exit /b 0

:NOADMIN
echo.
echo Elevation was refused or unavailable. Nothing was installed.
echo An administrator has to run this file once on this PC.
echo.
pause
exit /b 1

:RUN
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TimeFix.ps1"
echo.
pause
exit /b %errorlevel%
