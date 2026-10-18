@echo off
REM ============================================================
REM  WolfTeam Launcher - double-click this to start.
REM  Runs the PowerShell launcher with no install / no policy fuss.
REM  Keep WolfTeamLauncher.ps1 next to this file; the client folder comes from WT_CLIENT_DIR.
REM ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0WolfTeamLauncher.ps1"
