@echo off
rem Double-click to remove NetRoute and restore normal Windows networking.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-netroute.ps1"
