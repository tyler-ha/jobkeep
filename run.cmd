@echo off
REM  JobKeep -- start the whole local stack: Postgres, the API, the front end.
REM
REM    run.cmd                 everything, opens the browser
REM    run.cmd -NoFrontend     Postgres + API only
REM    run.cmd -NoBrowser      no browser
REM    run.cmd -Stop           tear down a stack left running by a crashed launcher
REM
REM  Ctrl+C stops what it started. cmd.exe will then ask "Terminate batch job
REM  (Y/N)?" -- answer either way, the PowerShell script has already cleaned up
REM  by the time the prompt appears.
REM
REM  The real script is scripts\run.ps1; this file exists so the stack can be
REM  started by double-click or from a plain cmd prompt.

setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run.ps1" %*

set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%
