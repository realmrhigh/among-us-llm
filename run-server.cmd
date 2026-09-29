@echo off
rem Build and start the Impostor server with the LLM bots. See run-server.ps1 for the options.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-server.ps1" %*
exit /b %ERRORLEVEL%
