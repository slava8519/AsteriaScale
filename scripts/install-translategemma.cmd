@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-translategemma.ps1" %*
set "rc=%errorlevel%"
if not "%rc%"=="0" pause
endlocal & exit /b %rc%
