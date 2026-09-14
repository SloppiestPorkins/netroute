@echo off
rem Builds dist\NetRoute-Setup-<version>.exe, one installer that needs nothing else on the PC.
rem No administrator rights needed. Pass -CertThumbprint <thumbprint> to sign it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
echo.
pause
