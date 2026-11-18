@echo off
call "%~dp0prepareDevelopment.bat" dotnet build -c Release %*
set "developmentExitCode=%errorlevel%"
pause
exit /b %developmentExitCode%
