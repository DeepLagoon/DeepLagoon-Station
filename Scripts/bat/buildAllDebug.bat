@echo off
call "%~dp0prepareDevelopment.bat" dotnet build -c Debug %*
set "developmentExitCode=%errorlevel%"
pause
exit /b %developmentExitCode%
