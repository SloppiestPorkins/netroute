<#
.SYNOPSIS
    Removes NetRoute and every piece of networking state it created (§44).

.DESCRIPTION
    Stops the service. Its shutdown resets the split-tunnel driver and restores Windows'
    default connection, and a local cleanup afterwards makes sure of both. Then it removes
    NetRoute's WFP sublayers, deletes the service, takes NetRoute off PATH and deletes the
    program folder.
    Your settings in %ProgramData%\NetRoute are kept unless you pass -RemoveSettings.
    Mullvad VPN and its driver are left installed; uninstall Mullvad VPN separately if you like.
#>
[CmdletBinding()]
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $extra = if ($RemoveSettings) { ' -RemoveSettings' } else { '' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`"$extra"
    exit
}

$target = Join-Path $env:ProgramFiles 'NetRoute'
$cli = Join-Path $target 'netroute.exe'

Get-Process -Name 'NetRoute.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\NetRoute.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'NetRoute.lnk') -Force -ErrorAction SilentlyContinue

$service = Get-Service -Name NetRoute -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name NetRoute -Force
        (Get-Service -Name NetRoute).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Write-Host 'Service stopped.'
}

if (Test-Path $cli) {
    & $cli cleanup-driver --remove-sublayers
}

if ($service) {
    & sc.exe delete NetRoute | Out-Null
    Write-Host 'Service removed.'
}

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$kept = ($machinePath -split ';') | Where-Object { $_ -and $_ -ne $target }
[Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'Machine')

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
if ($RemoveSettings) { Remove-Item (Join-Path $env:ProgramData 'NetRoute') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "`nNetRoute is uninstalled. Normal Windows networking is restored." -ForegroundColor Green
