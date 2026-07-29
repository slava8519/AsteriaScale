@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-sandwich.ps1" %*
exit /b %errorlevel%
