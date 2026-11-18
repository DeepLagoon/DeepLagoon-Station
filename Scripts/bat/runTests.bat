@echo off
if not exist "%~dp0..\logs" mkdir "%~dp0..\logs"
call "%~dp0prepareDevelopment.bat" dotnet test Content.Tests/Content.Tests.csproj -c DebugOpt -- NUnit.ConsoleOut=0 > "%~dp0..\logs\Content.Tests.log" 2>&1
set "developmentExitCode=%errorlevel%"
if not "%developmentExitCode%"=="0" type "%~dp0..\logs\Content.Tests.log"
pause
exit /b %developmentExitCode%
