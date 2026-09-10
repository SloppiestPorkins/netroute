#requires -version 5.1
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "NetRoute service removal requires an elevated PowerShell window."
    exit 1
}

& sc.exe stop NetRoute
& sc.exe delete NetRoute
exit $LASTEXITCODE
