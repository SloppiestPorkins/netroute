#requires -version 5.1
param([Parameter(Mandatory = $true)][string]$ExecutablePath)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "NetRoute service installation requires an elevated PowerShell window."
    exit 1
}

$resolved = (Resolve-Path -LiteralPath $ExecutablePath).Path
& sc.exe create NetRoute binPath= ('"' + $resolved + '"') start= auto obj= LocalSystem
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& sc.exe description NetRoute "Enforces NetRoute per-application network policy."
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& sc.exe failure NetRoute reset= 86400 actions= restart/5000/restart/5000/""/0
exit $LASTEXITCODE
