@echo off
setlocal
cd /d "%~dp0"
title ReportDesk Web Rollback
echo Run this file as Administrator on the IIS server.
echo Enter the exact backup directory printed by the update.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Apply-WebUpdate.ps1" -Rollback
set "REPORTDESK_EXIT=%ERRORLEVEL%"
echo.
echo Exit code: %REPORTDESK_EXIT%
pause
exit /b %REPORTDESK_EXIT%
