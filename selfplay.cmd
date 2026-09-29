@echo off
rem Watch bots play a full game against each other. See selfplay.ps1 for the options.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0selfplay.ps1" %*
exit /b %ERRORLEVEL%
