@echo off
REM Compatibility entry point. Use the same safe updater as update-plugins.bat.
call "%~dp0update-plugins.bat" %*
exit /b %ERRORLEVEL%
