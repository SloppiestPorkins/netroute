@echo off
rem Double-click to install or update NetRoute. It asks for admin rights itself.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-netroute.ps1"
