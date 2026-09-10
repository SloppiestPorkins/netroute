<#
.SYNOPSIS
    One-shot NetRoute setup and proof. Double-click RUN-NETROUTE-SETUP.cmd, or run this file.

.DESCRIPTION
    1. Re-enables the Ethernet adapter if Windows has it disabled, then waits for it to connect.
    2. Checks there are two working internet connections, which the proof needs.
    3. Installs Mullvad VPN if missing: winget first, otherwise the official installer after
       its signature has been verified. You don't need a Mullvad account. It is only there
       to put Mullvad's Microsoft-signed split-tunnel driver on this PC.
    4. Runs scripts\split-tunnel-setup.ps1. That verifies the driver's signature, disables
       Mullvad's own service, and starts the driver.
    5. Runs the proof (netroute-poc split) and saves its output to logs\ and the clipboard.

    Nothing here stops the split-tunnel driver, and nothing touches firewall rules.
    Everything is logged to logs\netroute-setup-<time>.log.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# --- elevate ----------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Asking Windows for administrator rights...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"")
    exit
}

$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Start-Transcript -Path (Join-Path $logDir "netroute-setup-$stamp.log") | Out-Null

function Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  OK  $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  !!  $text" -ForegroundColor Yellow }
function Fail([string]$text) {
    Write-Host "`n  FAILED: $text" -ForegroundColor Red
    Write-Host "  Full log: $logDir" -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}

Write-Host @'

  NetRoute setup
  --------------
  This will:
    1. re-enable your Ethernet adapter if it is disabled
    2. install Mullvad VPN (free app, no account needed) to get its signed driver
    3. disable Mullvad's own background service and start the driver for NetRoute
    4. run the proof that apps can be moved between your two connections

'@
Read-Host '  Press Enter to start, or close this window to cancel' | Out-Null

# --- 1. Ethernet ------------------------------------------------------------
Step '1. Ethernet'
$ethernet = Get-NetAdapter -Physical -ErrorAction SilentlyContinue |
    Where-Object { $_.InterfaceDescription -notmatch 'Wireless|Wi-?Fi|802\.11|Bluetooth' -and $_.MediaType -match '802\.3' } |
    Select-Object -First 1
if (-not $ethernet) {
    $ethernet = Get-NetAdapter -Name 'Ethernet' -ErrorAction SilentlyContinue
}
if (-not $ethernet) {
    Warn 'No wired Ethernet adapter found. The proof needs two internet connections.'
} else {
    if ($ethernet.Status -eq 'Disabled') {
        Write-Host "  Enabling $($ethernet.Name) ($($ethernet.InterfaceDescription))"
        Enable-NetAdapter -Name $ethernet.Name -Confirm:$false
    }
    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Seconds 2
        $cfg = Get-NetIPConfiguration -InterfaceAlias $ethernet.Name -ErrorAction SilentlyContinue
    } until (($cfg -and $cfg.IPv4DefaultGateway) -or (Get-Date) -gt $deadline)

    if ($cfg -and $cfg.IPv4DefaultGateway) {
        Ok "$($ethernet.Name) connected: $($cfg.IPv4Address.IPAddress) via $($cfg.IPv4DefaultGateway.NextHop)"
    } else {
        Warn "$($ethernet.Name) is enabled but has no internet route yet. Is the cable plugged in?"
    }
}

# --- 2. Two connections -----------------------------------------------------
Step '2. Internet connections'
$connections = Get-NetIPConfiguration | Where-Object {
    $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' -and
    $_.InterfaceDescription -notmatch 'Hyper-V|VMware|VirtualBox|WireGuard|Wintun|TAP-|Mullvad'
}
foreach ($c in $connections) { Ok "$($c.InterfaceAlias): $($c.IPv4Address.IPAddress)" }
if (@($connections).Count -lt 2) {
    Fail 'NetRoute needs two working internet connections (for example Ethernet and Wi-Fi). Connect both and run this again.'
}

# --- 3. Mullvad VPN ---------------------------------------------------------
Step '3. Mullvad VPN (for its signed driver)'
$mullvadDir = Join-Path $env:ProgramFiles 'Mullvad VPN'
if (Test-Path $mullvadDir) {
    Ok 'Already installed.'
} else {
    $installed = $false
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host '  Installing with winget (MullvadVPN.MullvadVPN)...'
        & winget install --id MullvadVPN.MullvadVPN -e --silent --accept-package-agreements --accept-source-agreements
        $installed = Test-Path $mullvadDir
    }
    if (-not $installed) {
        Write-Host '  Downloading the official installer from mullvad.net...'
        $installer = Join-Path $env:TEMP 'MullvadVPN-latest.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://mullvad.net/download/app/exe/latest' -OutFile $installer -UseBasicParsing
        $sig = Get-AuthenticodeSignature $installer
        $signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(none)' }
        if ($sig.Status -ne 'Valid' -or $signer -notmatch 'Mullvad|Amagicom') {
            Remove-Item $installer -Force -ErrorAction SilentlyContinue
            Fail "The downloaded installer is not validly signed by Mullvad ($($sig.Status), $signer). Not running it."
        }
        Ok "Installer signature valid: $signer"
        $p = Start-Process $installer -ArgumentList '/S' -Wait -PassThru
        Remove-Item $installer -Force -ErrorAction SilentlyContinue
        if ($p.ExitCode -ne 0) { Fail "Mullvad's installer exited with code $($p.ExitCode)." }
    }
    if (-not (Test-Path $mullvadDir)) { Fail 'Mullvad VPN did not install. Install it manually from https://mullvad.net/download and run this again.' }
    Ok 'Mullvad VPN installed. You do not need to sign in or connect.'
    Get-Process -Name 'Mullvad VPN' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# --- 4. Driver --------------------------------------------------------------
Step '4. Split-tunnel driver'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'split-tunnel-setup.ps1')
if ($LASTEXITCODE -ne 0) { Fail 'The driver could not be set up (see the messages above).' }

# --- 5. Proof ---------------------------------------------------------------
Step '5. Proof: can NetRoute move an app onto your other connection?'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET SDK is not installed, so the proof cannot be built.' }
$proofLog = Join-Path $logDir "split-proof-$stamp.txt"
Push-Location $root
# Windows PowerShell 5.1 turns any stderr line from a native program into a terminating
# error under 'Stop', so a harmless build warning would abort the script here.
$ErrorActionPreference = 'Continue'
try {
    & dotnet run --project (Join-Path $root 'src\NetRoute.Poc') -- split 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $proofLog
    $proofExit = $LASTEXITCODE
} finally {
    Pop-Location
    $ErrorActionPreference = 'Stop'
}
Get-Content $proofLog -Raw | Set-Clipboard

Write-Host ''
if ($proofExit -eq 0) {
    Write-Host '  PROOF PASSED: NetRoute can move apps between your connections.' -ForegroundColor Green
} else {
    Write-Host "  Proof did not pass (exit code $proofExit)." -ForegroundColor Yellow
}
Write-Host "  The proof output is on your clipboard. Paste it to Claude."
Write-Host "  Saved to: $proofLog"
Stop-Transcript | Out-Null
