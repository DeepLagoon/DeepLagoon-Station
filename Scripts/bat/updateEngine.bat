@echo off
call "%~dp0prepareDevelopment.bat" %*
exit /b %errorlevel%
