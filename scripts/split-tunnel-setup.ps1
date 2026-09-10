<#
.SYNOPSIS
    Hands Mullvad VPN's Microsoft-signed split-tunnel driver over to NetRoute.

.DESCRIPTION
    NetRoute uses Mullvad's open-source split-tunnel driver (github.com/mullvad/win-split-tunnel,
    MPL-2.0 / GPL-3.0) to move apps onto a chosen adapter. See docs/SPLIT-TUNNEL.md.

    This script:
      1. finds mullvad-split-tunnel.sys in the Mullvad VPN install folder;
      2. refuses to continue unless its Authenticode signature is Valid and from Mullvad/Amagicom;
      3. stops and disables Mullvad's own background service. The driver's device allows only
         one client, and NetRoute has to be that client;
      4. registers the driver as a demand-start kernel service (reusing Mullvad's service name
         if it already exists) and starts it.

    It never stops a running driver. Stopping the driver without resetting it first makes the
    driver crash Windows deliberately.

    Run from an elevated Windows PowerShell 5.1 prompt.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Please run this from an administrator PowerShell window.' -ForegroundColor Red
    exit 2
}

$roots = @("$env:ProgramFiles\Mullvad VPN", "${env:ProgramFiles(x86)}\Mullvad VPN") | Where-Object { Test-Path $_ }
if (-not $roots) {
    Write-Host 'Mullvad VPN is not installed. Install it from https://mullvad.net/download (no account needed), then re-run.' -ForegroundColor Red
    exit 1
}

$sys = $roots | ForEach-Object { Get-ChildItem $_ -Recurse -Filter 'mullvad-split-tunnel.sys' -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $sys) {
    Write-Host 'Could not find mullvad-split-tunnel.sys under the Mullvad VPN folder.' -ForegroundColor Red
    exit 1
}
Write-Host "Driver: $($sys.FullName)"

$signature = Get-AuthenticodeSignature $sys.FullName
$signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(none)' }
Write-Host "Signature: $($signature.Status), signer: $signer"
if ($signature.Status -ne 'Valid' -or $signer -notmatch 'Mullvad|Amagicom') {
    Write-Host 'Refusing to load a driver that is not validly signed by Mullvad.' -ForegroundColor Red
    exit 1
}

# Mullvad's daemon would otherwise hold the driver's only handle.
Get-Service | Where-Object { $_.DisplayName -match 'Mullvad' -and $_.Name -notmatch 'split' } | ForEach-Object {
    Write-Host "Stopping and disabling $($_.Name) ($($_.DisplayName))"
    if ($_.Status -ne 'Stopped') { Stop-Service $_.Name -Force }
    Set-Service $_.Name -StartupType Disabled
}

$serviceName = 'mullvad-split-tunnel'
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Registering kernel service $serviceName"
    & sc.exe create $serviceName type= kernel start= demand binPath= "$($sys.FullName)" DisplayName= 'Mullvad Split Tunnel (used by NetRoute)' | Out-Host
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

$service = Get-Service -Name $serviceName
if ($service.Status -ne 'Running') {
    Write-Host "Starting $serviceName"
    & sc.exe start $serviceName | Out-Host
    Start-Sleep -Seconds 2
}

$service = Get-Service -Name $serviceName
Write-Host "Driver service state: $($service.Status)"
if ($service.Status -ne 'Running') {
    Write-Host 'The driver did not start. Check Event Viewer > Windows Logs > System for the reason.' -ForegroundColor Red
    exit 1
}

Write-Host "`nReady. Now run:  dotnet run --project src\NetRoute.Poc -- split" -ForegroundColor Green
