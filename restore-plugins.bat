@echo off
call "%~dp0update-plugins.bat" -RestoreLatest %*
exit /b %ERRORLEVEL%
