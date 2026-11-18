@echo off
if not exist "%~dp0..\logs" mkdir "%~dp0..\logs"
call "%~dp0prepareDevelopment.bat" dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt -- NUnit.ConsoleOut=0 NUnit.MapWarningTo=Failed > "%~dp0..\logs\Content.IntegrationTests.log" 2>&1
set "developmentExitCode=%errorlevel%"
if not "%developmentExitCode%"=="0" type "%~dp0..\logs\Content.IntegrationTests.log"
pause
exit /b %developmentExitCode%
