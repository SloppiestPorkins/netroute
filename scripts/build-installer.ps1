<#
.SYNOPSIS
    Builds dist\NetRoute-Setup-<version>.exe: one installer that needs nothing else on the PC.

.DESCRIPTION
    Double-click BUILD-INSTALLER.cmd, or run this file. Needs the .NET 8 SDK to build; does
    not need administrator rights.

    1. Publishes the service, the CLI and the app self-contained for win-x64 into one folder,
       so the target PC doesn't need .NET installed.
    2. Adds Mullvad's split-tunnel driver from third_party\, after checking its signature.
    3. Builds the uninstaller (the setup program with no payload) into that folder.
    4. Zips the folder and builds the setup program with the zip embedded.
    5. Signs everything, when given a certificate.

    Signing: unsigned, the installer works, but on other people's PCs Windows SmartScreen says
    "Windows protected your PC" until the file builds a reputation. Pass -CertThumbprint (a
    code-signing certificate in your certificate store) or -PfxPath to sign.

.EXAMPLE
    .\build-installer.ps1
.EXAMPLE
    .\build-installer.ps1 -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string]$CertThumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'artifacts\installer'
$payload = Join-Path $work 'payload'
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$sign = [bool]($CertThumbprint -or $PfxPath)

function Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host "`n  FAILED: $text" -ForegroundColor Red; exit 1 }

function Invoke-Dotnet([string[]]$Arguments) {
    # dotnet writes progress to stderr; Windows PowerShell 5.1 would treat that as fatal.
    $ErrorActionPreference = 'Continue'
    & dotnet @Arguments 2>&1 | ForEach-Object { "    $_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) { Fail "dotnet $($Arguments[0]) $($Arguments[1]) failed (see above)." }
}

function Sign-Files([string[]]$Files) {
    if (-not $sign -or -not $Files) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { Fail 'signtool.exe not found. Install the Windows SDK, or build without signing.' }
    $identity = if ($PfxPath) {
        @('/f', $PfxPath) + $(if ($PfxPassword) { @('/p', $PfxPassword) } else { @() })
    } else {
        @('/sha1', $CertThumbprint)
    }
    & $signtool.FullName sign /fd sha256 /tr $TimestampUrl /td sha256 @identity @Files
    if ($LASTEXITCODE -ne 0) { Fail 'Signing failed (see above).' }
}

Write-Host "Building the NetRoute $version installer ($(if ($sign) { 'signed' } else { 'unsigned' }))"

# Take turns with INSTALL-NETROUTE and RUN-NETROUTE-SETUP: parallel builds of the same projects fight.
$buildLock = New-Object System.Threading.Mutex($false, 'Global\NetRoute.Build')
if (-not $buildLock.WaitOne([TimeSpan]::FromMinutes(10))) { Fail 'Another NetRoute build has been running for 10 minutes. Close it and try again.' }
try {
    Step '1. Publishing NetRoute (self-contained, win-x64)'
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    foreach ($project in 'src\NetRoute.Service', 'src\NetRoute.Cli', 'src\NetRoute.App') {
        Write-Host "  $project"
        Invoke-Dotnet @('publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-o', $payload, '-p:SatelliteResourceLanguages=en', '-p:DebugType=None', '-p:DebugSymbols=false', '--nologo')
    }

    Step "2. Mullvad's split-tunnel driver"
    $driver = Join-Path $root 'third_party\mullvad-split-tunnel\mullvad-split-tunnel.sys'
    if (-not (Test-Path $driver)) { Fail "Missing $driver. See third_party\mullvad-split-tunnel\README.md." }
    $signature = Get-AuthenticodeSignature $driver
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(none)' }
    if ($signature.Status -ne 'Valid' -or $signer -notmatch 'Mullvad|Amagicom') { Fail "The driver isn't validly signed by Mullvad ($($signature.Status), $signer)." }
    New-Item -ItemType Directory -Force -Path (Join-Path $payload 'driver') | Out-Null
    Copy-Item $driver (Join-Path $payload 'driver')
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $payload
    Write-Host "  Version $((Get-Item $driver).VersionInfo.FileVersion), signed by $($signer.Split(',')[0])"

    Step '3. Uninstaller'
    $uninstallerOut = Join-Path $work 'uninstaller'
    Invoke-Dotnet @('build', (Join-Path $root 'src\NetRoute.Setup'), '-c', 'Release', '-o', $uninstallerOut, '--no-incremental', '--nologo', '-p:PayloadZip=')
    Copy-Item (Join-Path $uninstallerOut 'NetRoute-Setup.exe') (Join-Path $payload 'Uninstall NetRoute.exe')
    Sign-Files @(Get-ChildItem $payload -File | Where-Object { $_.Name -match '^(NetRoute.*|netroute|Uninstall NetRoute)\.(exe|dll)$' } | ForEach-Object { $_.FullName })

    Step '4. Packing'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $work 'payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host ("  {0:N0} files, {1:N1} MB packed" -f @(Get-ChildItem $payload -Recurse -File).Count, ((Get-Item $zip).Length / 1MB))

    Step '5. Setup program'
    $setupOut = Join-Path $work 'setup'
    Invoke-Dotnet @('build', (Join-Path $root 'src\NetRoute.Setup'), '-c', 'Release', '-o', $setupOut, '--no-incremental', '--nologo', "-p:PayloadZip=$zip")
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $setup = Join-Path $dist "NetRoute-Setup-$version.exe"
    Copy-Item (Join-Path $setupOut 'NetRoute-Setup.exe') $setup -Force
    Sign-Files @($setup)
} finally {
    $buildLock.ReleaseMutex()
    $buildLock.Dispose()
}

Step 'Done'
$item = Get-Item $setup
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ("  {0}  ({1:N1} MB)" -f $item.FullName, ($item.Length / 1MB)) -ForegroundColor Green
Write-Host "  SHA-256 $hash"

# The update document, ready to publish beside the installer. NetRoute refuses to fetch a build
# whose sha256 it cannot check, so this file is what makes in-app updating work at all: point
# 'netroute update --feed' at wherever this ends up.
$feed = Join-Path $dist 'updates.json'
[ordered]@{
    version = $version
    url     = "https://example.invalid/netroute/$($item.Name)"
    sha256  = $hash
    size    = $item.Length
    notes   = 'Edit this line: it is what the app shows beside the version.'
} | ConvertTo-Json | ForEach-Object { [IO.File]::WriteAllText($feed, $_, (New-Object Text.UTF8Encoding $false)) }
Write-Host "  Update document $feed (set its url before publishing)"
if (-not $sign) {
    Write-Host '  Unsigned: SmartScreen will warn on other PCs. See the notes at the top of build-installer.ps1.' -ForegroundColor Yellow
}
