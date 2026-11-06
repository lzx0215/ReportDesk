@echo off
setlocal
cd /d "%~dp0"
title ReportDesk Read-Only Diagnostic
echo Run as Administrator on the IIS server.
echo Site: C:\a_userspace\ReportTool\ReportDesk-Web
echo This tool does not update files, change configuration, restart IIS or connect to Oracle.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose-WebUpdate.ps1"
set "REPORTDESK_DIAG_EXIT=%ERRORLEVEL%"
echo.
echo Exit code: %REPORTDESK_DIAG_EXIT%
pause
exit /b %REPORTDESK_DIAG_EXIT%
