@echo off
setlocal
cd /d "%~dp0"
title ReportDesk Web Update Check
echo Run this file as Administrator on the IIS server. Read-only checks only.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Apply-WebUpdate.ps1" -CheckOnly
set "REPORTDESK_EXIT=%ERRORLEVEL%"
echo.
echo Exit code: %REPORTDESK_EXIT%
pause
exit /b %REPORTDESK_EXIT%
