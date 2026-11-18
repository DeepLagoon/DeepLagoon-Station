@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\prepare-development.ps1" %*
exit /b %errorlevel%
