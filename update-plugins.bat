@echo off
setlocal
REM Use the built-in Windows PowerShell; no extra runtime is needed.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Sync-Plugins.ps1" %*
set "SYNC_EXIT=%ERRORLEVEL%"
echo.
echo Exit code: %SYNC_EXIT%  [0=success, 1=failed, 2=some sources skipped]
echo Reports and backups: %~dp0.plugin-sync
if not defined PLUGIN_SYNC_NO_PAUSE pause
exit /b %SYNC_EXIT%
