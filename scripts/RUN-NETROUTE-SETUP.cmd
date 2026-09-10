@echo off
rem Double-click to run the whole NetRoute setup and proof. It asks for admin rights itself.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-netroute-setup.ps1"
