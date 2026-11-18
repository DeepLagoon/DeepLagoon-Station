@echo off
call "%~dp0prepareDevelopment.bat" dotnet build -c Tools %*
set "developmentExitCode=%errorlevel%"
pause
exit /b %developmentExitCode%
