@echo off
REM Double-click launcher for the DiveBind installer. Runs install.ps1 next to this file,
REM bypassing PowerShell's execution policy for this one script only. Quoting of "%~dp0"
REM keeps it working even from a path with spaces or parentheses.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
