<#
.SYNOPSIS
    Installs or updates NetRoute: builds it, installs the Windows service, and puts `netroute` on PATH.

.DESCRIPTION
    Double-click INSTALL-NETROUTE.cmd, or run this file. Safe to run again to update.

    The new version is built into a temporary folder while the current service keeps
    running. Only after a successful build is the service stopped, the files swapped and
    the service started again, and the restart happens even if the swap fails. An earlier
    version stopped the service first, so a build problem could leave the PC unprotected
    with the service stopped.

    1. Builds the service, the CLI and the app (a shared lock stops two NetRoute builds racing).
    2. Stops the service. Its shutdown resets the split-tunnel driver and restores Windows'
       default connection. Then copies the new files into "C:\Program Files\NetRoute" and
       starts the service again.
    3. Installs the service if it isn't installed (LocalSystem, automatic start, restart on failure).
    4. Adds the install folder to the machine PATH.
    5. Adds a Start menu entry and a sign-in shortcut that starts NetRoute in the tray.
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
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Start-Transcript -Path (Join-Path $logDir "install-$stamp.log") | Out-Null

$target = Join-Path $env:ProgramFiles 'NetRoute'
$stage = Join-Path $env:TEMP "NetRoute-build-$stamp"
$exe = Join-Path $target 'NetRoute.Service.exe'
$script:serviceStoppedByUs = $false

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }

function Start-NetRouteService {
    if (Get-Service -Name NetRoute -ErrorAction SilentlyContinue) {
        Start-Service -Name NetRoute -ErrorAction SilentlyContinue
        try { (Get-Service -Name NetRoute).WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) } catch { }
    }
}

function Fail([string]$t) {
    Write-Host "`n  FAILED: $t" -ForegroundColor Red
    if ($script:serviceStoppedByUs) {
        Write-Host '  Restarting the NetRoute service so your PC stays protected...' -ForegroundColor Yellow
        Start-NetRouteService
    }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Full log: $logDir" -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}

# --- 1. Build, while the current version keeps running --------------------
Step '1. Building NetRoute (your current NetRoute keeps running meanwhile)'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET SDK is not installed.' }

# Two builds of the same projects at once (for example this and RUN-NETROUTE-SETUP) fight
# over the same obj folders and can hang or fail. Take turns.
$buildLock = New-Object System.Threading.Mutex($false, 'Global\NetRoute.Build')
if (-not $buildLock.WaitOne([TimeSpan]::FromMinutes(10))) { Fail 'Another NetRoute build has been running for 10 minutes. Close it and try again.' }
try {
    $ErrorActionPreference = 'Continue'   # dotnet writes progress to stderr; don't treat it as fatal
    foreach ($project in 'src\NetRoute.Service', 'src\NetRoute.Cli', 'src\NetRoute.App') {
        Write-Host "  Building $project"
        & dotnet publish (Join-Path $root $project) -c Release -o $stage --nologo 2>&1 | ForEach-Object { "    $_" }
        if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = 'Stop'; Fail "Building $project failed (see above). Your current NetRoute was left running." }
    }
    $ErrorActionPreference = 'Stop'
} finally {
    $buildLock.ReleaseMutex()
    $buildLock.Dispose()
}
Write-Host '  Build succeeded.'

# --- 2. Swap ----------------------------------------------------------------
Step '2. Installing the new version'
Get-Process -Name 'NetRoute.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$service = Get-Service -Name NetRoute -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name NetRoute -Force
    (Get-Service -Name NetRoute).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $script:serviceStoppedByUs = $true
    Write-Host '  Service stopped for the update.'
}
try {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $target -Recurse -Force
} catch {
    Fail "Copying the new files failed: $($_.Exception.Message)"
}
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  Installed to $target"

# --- 3. Service ---------------------------------------------------------------
Step '3. Starting the service'
if (-not (Get-Service -Name NetRoute -ErrorAction SilentlyContinue)) {
    New-Service -Name NetRoute -BinaryPathName "`"$exe`"" -DisplayName 'NetRoute' -StartupType Automatic `
        -Description 'Keeps your apps on the network you chose.' | Out-Null
    & sc.exe failure NetRoute reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
    Write-Host '  Service created (runs as LocalSystem, starts with Windows, restarts on failure).'
}
Start-NetRouteService
$script:serviceStoppedByUs = $false
if ((Get-Service -Name NetRoute).Status -ne 'Running') {
    Fail 'The service did not start. Check Event Viewer > Windows Logs > Application, source NetRoute.Service.'
}
Write-Host '  Service running.'

# --- 4. PATH ----------------------------------------------------------------
Step '4. Adding netroute to PATH'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (($machinePath -split ';') -notcontains $target) {
    [Environment]::SetEnvironmentVariable('Path', "$machinePath;$target", 'Machine')
    Write-Host '  Added. Open a NEW terminal to use `netroute`.'
} else {
    Write-Host '  Already on PATH.'
}

# --- 5. Shortcuts -------------------------------------------------------------
Step '5. Shortcuts'
$appExe = Join-Path $target 'NetRoute.App.exe'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\NetRoute.lnk'))
$link.TargetPath = $appExe
$link.WorkingDirectory = $target
$link.Description = 'Keep your apps on the network you chose'
$link.Save()
# Start with Windows, straight to the tray (§41).
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'NetRoute.lnk'))
$link.TargetPath = $appExe
$link.Arguments = '--minimized'
$link.WorkingDirectory = $target
$link.Save()
Write-Host '  Start menu shortcut added; NetRoute starts in the tray when you sign in.'

Step 'Status'
& (Join-Path $target 'netroute.exe') status

# Open the app as the signed-in user, not elevated: explorer launches it outside this admin session.
Start-Process explorer.exe -ArgumentList "`"$appExe`""

Write-Host @"

  NetRoute is updated and running, and the NetRoute app is opening.

"@ -ForegroundColor Green
Stop-Transcript | Out-Null
