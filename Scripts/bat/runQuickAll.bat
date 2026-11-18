@echo off
call "%~dp0prepareDevelopment.bat"
if errorlevel 1 exit /b %errorlevel%
start "DeepLagoon Server" "%~dp0runQuickServer.bat" %*
start "DeepLagoon Client" "%~dp0runQuickClient.bat" %*
