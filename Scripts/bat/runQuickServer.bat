@echo off
call "%~dp0prepareDevelopment.bat" dotnet run --project Content.Server --no-build %*
set "developmentExitCode=%errorlevel%"
pause
exit /b %developmentExitCode%
