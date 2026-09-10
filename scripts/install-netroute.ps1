<#
.SYNOPSIS
    Installs or updates NetRoute: builds it, installs the Windows service, and puts `netroute` on PATH.

.DESCRIPTION
    Double-click INSTALL-NETROUTE.cmd, or run this file. Safe to run again to update.
    1. Stops the running NetRoute service, if any. Its shutdown resets the split-tunnel driver
       and restores Windows' default connection, so the machine is back to normal.
    2. Publishes the service and the CLI to "C:\Program Files\NetRoute".
    3. Installs the service (LocalSystem, automatic start, restart on failure) and starts it.
    4. Adds the install folder to the machine PATH, so `netroute` works in new terminals.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"")
    exit
}

$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir ("install-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))) | Out-Null

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Fail([string]$t) { Write-Host "`n  FAILED: $t" -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }

$target = Join-Path $env:ProgramFiles 'NetRoute'
$exe = Join-Path $target 'NetRoute.Service.exe'

Step '1. Stopping the current NetRoute service'
$service = Get-Service -Name NetRoute -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name NetRoute -Force
    (Get-Service -Name NetRoute).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    Write-Host '  Stopped.'
} else {
    Write-Host '  Not running.'
}

Step '2. Building NetRoute'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET SDK is not installed.' }
$ErrorActionPreference = 'Continue'   # dotnet writes progress to stderr; don't treat it as fatal
foreach ($project in 'src\NetRoute.Service', 'src\NetRoute.Cli') {
    & dotnet publish (Join-Path $root $project) -c Release -o $target --nologo -v q 2>&1 | ForEach-Object { "  $_" }
    if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = 'Stop'; Fail "Building $project failed." }
}
$ErrorActionPreference = 'Stop'
Write-Host "  Installed to $target"

Step '3. Installing the service'
if (-not (Get-Service -Name NetRoute -ErrorAction SilentlyContinue)) {
    New-Service -Name NetRoute -BinaryPathName "`"$exe`"" -DisplayName 'NetRoute' -StartupType Automatic `
        -Description 'Keeps your apps on the network you chose.' | Out-Null
    & sc.exe failure NetRoute reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
    Write-Host '  Service created (runs as LocalSystem, starts with Windows, restarts on failure).'
}
Start-Service -Name NetRoute
(Get-Service -Name NetRoute).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host '  Service running.'

Step '4. Adding netroute to PATH'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (($machinePath -split ';') -notcontains $target) {
    [Environment]::SetEnvironmentVariable('Path', "$machinePath;$target", 'Machine')
    Write-Host '  Added. Open a NEW terminal to use `netroute`.'
} else {
    Write-Host '  Already on PATH.'
}
$env:Path = "$env:Path;$target"

Step 'Status'
& (Join-Path $target 'netroute.exe') status

Write-Host @"

  NetRoute is installed and running.

  In a NEW terminal:
    netroute adapters                          see your connections
    netroute setup Ethernet "Wi-Fi 2"           Gaming = Ethernet, Downloads = Wi-Fi 2
    netroute add Steam downloads
    netroute add "Halo Infinite" gaming         a Steam, Game Pass or any installed game
    netroute status                             check it's working
    netroute emergency-disable                  put everything back to normal immediately

"@ -ForegroundColor Green
Stop-Transcript | Out-Null
