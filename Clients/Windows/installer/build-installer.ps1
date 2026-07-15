<#
.SYNOPSIS
    Builds the Valenius installer.

.DESCRIPTION
    1. Generates logo.ico and wizard images from logo.png  (create-assets.ps1)
    2. Fetches WireGuard binaries for x64 and arm64
    3. dotnet publish (self-contained, win-x64 + win-arm64, Release) for Service and TrayApp
    4. Patches the version number in the .iss script
    5. Runs Inno Setup to produce installer\output\ValeniusSetup-<version>.exe

.PARAMETER Version
    Version string embedded in the installer, e.g. "1.2.0".
    Defaults to "1.0.0".

.PARAMETER InnoSetupPath
    Path to the Inno Setup compiler (ISCC.exe).
    Defaults to the standard Inno Setup 6 installation path.

.EXAMPLE
    .\build-installer.ps1 -Version "1.1.0"
#>
param(
    [string] $Version        = '1.0.0',
    [string] $InnoSetupPath  = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',

    # Code signing (optional).  Requires signtool.exe (Windows SDK).
    #
    # A) Local certificate (cert in the Windows store - e.g. OV .pfx imported, or EV on a token):
    #   .\build-installer.ps1 -Version "1.2.0" -Sign
    #   .\build-installer.ps1 -Version "1.2.0" -Sign -CertThumbprint "AABBCC..."   # pick one
    #
    # B) Azure Trusted Signing (Microsoft cloud service - no .pfx/thumbprint). Needs the
    #    Trusted Signing dlib (from the Microsoft.Trusted.Signing.Client NuGet package) and a
    #    metadata JSON describing the account/profile, plus an authenticated Azure context
    #    (az login, or AZURE_* service-principal env vars, or a managed identity in CI):
    #   .\build-installer.ps1 -Version "1.2.0" -TrustedSigning `
    #       -TsDlib "C:\ts\bin\x64\Azure.CodeSigning.Dlib.dll" -TsMetadata "C:\ts\metadata.json"
    [switch] $Sign,
    [string] $CertThumbprint = '',
    [string] $TimestampUrl   = 'http://timestamp.digicert.com',
    [string] $SignToolPath   = '',  # auto-detected if empty

    # Azure Trusted Signing. -TrustedSigning implies -Sign.
    [switch] $TrustedSigning,
    [string] $TsDlib         = '',  # path to Azure.CodeSigning.Dlib.dll
    [string] $TsMetadata     = ''   # path to the Trusted Signing metadata JSON
)

# Trusted Signing is a signing method, so it turns signing on.
if ($TrustedSigning) { $Sign = $true }

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$LASTEXITCODE = 0   # initialise so strict mode doesn't complain before first native call

# -- Signing helpers -----------------------------------------------------------

function Find-SignTool {
    if ($SignToolPath -and (Test-Path $SignToolPath)) { return $SignToolPath }
    # Search all installed Windows SDK versions, newest first.
    $sdkBase = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $sdkBase) {
        $found = Get-ChildItem $sdkBase -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -match '\\x64\\' } |
                 Sort-Object FullName -Descending |
                 Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    # Windows SDK build tools shipped via NuGet (e.g. bundled with Visual Studio) - signtool
    # often lives here rather than in Windows Kits\10\bin on dev boxes.
    foreach ($nugetBase in @(
        'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools',
        "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools")) {
        if (Test-Path $nugetBase) {
            $found = Get-ChildItem $nugetBase -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
                     Where-Object { $_.FullName -match '\\x64\\' } |
                     Sort-Object FullName -Descending |
                     Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }
    # Fall back to PATH
    $inPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($inPath) { return $inPath.Source }
    return $null
}

# Azure Trusted Signing's dlib authenticates via the Azure CLI credential, so `az` must be
# callable by the signtool process. winget's PATH update isn't always visible to a freshly
# spawned shell, so locate az.cmd and prepend it if it isn't already resolvable.
function Ensure-AzOnPath {
    if (Get-Command 'az' -ErrorAction SilentlyContinue) { return }
    $azDir = @(
        "$env:ProgramFiles\Microsoft SDKs\Azure\CLI2\wbin",
        "${env:ProgramFiles(x86)}\Microsoft SDKs\Azure\CLI2\wbin") |
        Where-Object { Test-Path (Join-Path $_ 'az.cmd') } | Select-Object -First 1
    if ($azDir) { $env:PATH = "$azDir;$env:PATH" }
    else { Write-Warning "Azure CLI (az) not found on PATH - Trusted Signing auth may fail. Run 'az login' first." }
}

function Invoke-Sign {
    param([string[]] $Files)
    $tool = Find-SignTool
    if (-not $tool) {
        Write-Error 'signtool.exe not found. Install the Windows SDK or pass -SignToolPath.'
        exit 1
    }
    if ($TrustedSigning) {
        Ensure-AzOnPath
        if (-not (Test-Path $TsDlib)) {
            Write-Error "Trusted Signing dlib not found: '$TsDlib'. Install the Microsoft.Trusted.Signing.Client NuGet package and point -TsDlib at its bin\x64\Azure.CodeSigning.Dlib.dll."
            exit 1
        }
        if (-not (Test-Path $TsMetadata)) {
            Write-Error "Trusted Signing metadata JSON not found: '$TsMetadata'."
            exit 1
        }
        # Microsoft's timestamp service (unless the caller overrode the default).
        $ts = if ($TimestampUrl -eq 'http://timestamp.digicert.com') { 'http://timestamp.acs.microsoft.com' } else { $TimestampUrl }
        foreach ($file in $Files) {
            Write-Host "  Signing (Azure Trusted Signing): $file"
            & $tool sign /v /fd SHA256 /td SHA256 /tr $ts /dlib $TsDlib /dmdf $TsMetadata $file
            if ($LASTEXITCODE -ne 0) { Write-Error "Signing failed for: $file"; exit 1 }
        }
        return
    }

    $certArgs = if ($CertThumbprint) { @('/sha1', $CertThumbprint) } else { @('/a') }
    foreach ($file in $Files) {
        Write-Host "  Signing: $file"
        & $tool sign /fd SHA256 /td SHA256 /tr $TimestampUrl @certArgs $file
        if ($LASTEXITCODE -ne 0) { Write-Error "Signing failed for: $file"; exit 1 }
    }
}

# -- Paths ---------------------------------------------------------------------
$InstallerDir = $PSScriptRoot                                               # Clients\Windows\installer
$ClientDir    = Split-Path $InstallerDir -Parent                            # Clients\Windows
$RepoRoot     = Split-Path (Split-Path $ClientDir -Parent) -Parent          # repo root (Valenius\)
$PublishDir   = Join-Path $InstallerDir 'publish'
$ServiceSrc   = Join-Path $ClientDir 'src\Valenius.Service\Valenius.Service.csproj'
$TrayAppSrc   = Join-Path $ClientDir 'src\Valenius.TrayApp\Valenius.TrayApp.csproj'
$IssFile      = Join-Path $InstallerDir 'Valenius.iss'
$OutputDir    = Join-Path $InstallerDir 'output'

# -- Verify prerequisites ------------------------------------------------------
if (-not (Test-Path $InnoSetupPath)) {
    Write-Error @"
Inno Setup compiler not found at:
  $InnoSetupPath

Download and install Inno Setup 6 from https://jrsoftware.org/isinfo.php
then re-run this script (or pass -InnoSetupPath to point to your ISCC.exe).
"@
    exit 1
}

if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    Write-Error '.NET SDK not found in PATH. Install from https://dotnet.microsoft.com/'
    exit 1
}

# -- Fetch WireGuard binaries (x64 + arm64) -----------------------------------
#
# WireGuard does not publish to GitHub releases, so we cannot auto-download.
# x64  : copied automatically from the local WireGuard installation.
# arm64 : must be placed manually once in installer\wireguard\arm64\
#         - Download the ARM64 installer from https://www.wireguard.com/install/
#         - Run: msiexec /a wireguard-arm64.msi /qn TARGETDIR=C:\wg-extract
#         - Copy wireguard.exe and wintun.dll from C:\wg-extract into the folder above.
#
Write-Host "`n=== Preparing WireGuard binaries ===" -ForegroundColor Cyan
$WireGuardDir = Join-Path $InstallerDir 'wireguard'

foreach ($arch in @('x64', 'arm64')) {
    $archDir = Join-Path $WireGuardDir $arch
    $exePath = Join-Path $archDir 'wireguard.exe'
    New-Item $archDir -ItemType Directory -Force | Out-Null

    if (Test-Path $exePath) {
        Write-Host "  [$arch] OK - $((Get-Item $exePath).VersionInfo.FileVersion)"
        continue
    }

    # x64: copy from local WireGuard installation on the build machine.
    if ($arch -eq 'x64') {
        $localExe = Join-Path $env:ProgramFiles 'WireGuard\wireguard.exe'
        if (Test-Path $localExe) {
            Write-Host "  [$arch] Copying from local WireGuard installation..."
            Copy-Item $localExe $exePath -Force
            Write-Host "  [$arch] OK - $((Get-Item $exePath).VersionInfo.FileVersion)"
            continue
        }
    }

    Write-Error @"
[$arch] WireGuard binary not found in: $archDir

To add it:
  1. Install WireGuard from https://www.wireguard.com/install/
     (x64: the installer copies wireguard.exe to %ProgramFiles%\WireGuard\)
  2. Or copy wireguard.exe manually into: $archDir
  The file is cached there and only needs to be added once.
"@
    exit 1
}

# -- Regenerate image assets ---------------------------------------------------
Write-Host "`n=== Generating image assets ===" -ForegroundColor Cyan
& (Join-Path $InstallerDir 'create-assets.ps1')
if (-not $?) { Write-Error 'Asset generation failed'; exit 1 }

# -- Clean publish output ------------------------------------------------------
Write-Host "`n=== Cleaning previous publish output ===" -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
foreach ($d in 'service-x64', 'service-arm64', 'trayapp-x64', 'trayapp-arm64') {
    New-Item (Join-Path $PublishDir $d) -ItemType Directory | Out-Null
}
New-Item $OutputDir -ItemType Directory -Force | Out-Null

# -- Publish Windows Service (x64 + arm64) ------------------------------------
foreach ($arch in @('x64', 'arm64')) {
    Write-Host "`n=== Publishing Service ($arch) ===" -ForegroundColor Cyan
    dotnet publish $ServiceSrc `
        --configuration Release `
        --runtime win-$arch `
        --self-contained true `
        /p:Version=$Version `
        --output (Join-Path $PublishDir "service-$arch")
    if ($LASTEXITCODE -ne 0) { Write-Error "Service ($arch) publish failed"; exit 1 }
    $outDir = Join-Path $PublishDir "service-$arch"
    if ((Get-ChildItem $outDir -File -Recurse).Count -eq 0) {
        Write-Error "Service ($arch) publish produced no output in: $outDir"; exit 1
    }
}

# -- Publish TrayApp (x64 + arm64) --------------------------------------------
foreach ($arch in @('x64', 'arm64')) {
    Write-Host "`n=== Publishing TrayApp ($arch) ===" -ForegroundColor Cyan
    dotnet publish $TrayAppSrc `
        --configuration Release `
        --runtime win-$arch `
        --self-contained true `
        /p:Version=$Version `
        --output (Join-Path $PublishDir "trayapp-$arch")
    if ($LASTEXITCODE -ne 0) { Write-Error "TrayApp ($arch) publish failed"; exit 1 }
    $outDir = Join-Path $PublishDir "trayapp-$arch"
    if ((Get-ChildItem $outDir -File -Recurse).Count -eq 0) {
        Write-Error "TrayApp ($arch) publish produced no output in: $outDir"; exit 1
    }
}

# -- Sign inner EXEs (before Inno Setup packages them) ------------------------
if ($Sign) {
    Write-Host "`n=== Signing inner executables ===" -ForegroundColor Cyan
    $innerExes = @(
        (Join-Path $PublishDir 'service-x64\Valenius.Service.exe'),
        (Join-Path $PublishDir 'service-arm64\Valenius.Service.exe'),
        (Join-Path $PublishDir 'trayapp-x64\Valenius.TrayApp.exe'),
        (Join-Path $PublishDir 'trayapp-arm64\Valenius.TrayApp.exe')
    )
    Invoke-Sign $innerExes
}

# -- Patch version in .iss script ---------------------------------------------
Write-Host "`n=== Patching version in $IssFile ===" -ForegroundColor Cyan
$iss = Get-Content $IssFile -Raw -Encoding UTF8
$iss = $iss -replace '(?m)^(#define MyAppVersion\s+)"[^"]+"', "`$1`"$Version`""
Set-Content $IssFile $iss -Encoding UTF8 -NoNewline
Write-Host "  Version set to $Version"

# -- Run Inno Setup ------------------------------------------------------------
Write-Host "`n=== Running Inno Setup ===" -ForegroundColor Cyan
& $InnoSetupPath $IssFile
if ($LASTEXITCODE -ne 0) { Write-Error 'Inno Setup compilation failed'; exit 1 }

# -- Sign the installer EXE ----------------------------------------------------
$installer = Get-ChildItem $OutputDir -Filter "ValeniusSetup-*.exe" |
             Sort-Object LastWriteTime -Descending |
             Select-Object -First 1

if ($Sign) {
    if (-not $installer) { Write-Error 'Installer EXE not found - cannot sign.'; exit 1 }
    Write-Host "`n=== Signing installer ===" -ForegroundColor Cyan
    Invoke-Sign @($installer.FullName)
}

# -- Compute SHA-256 and update versions.json ---------------------------------
# Re-query after signing so the hash covers the signed bytes.
$installer = Get-ChildItem $OutputDir -Filter "ValeniusSetup-*.exe" |
             Sort-Object LastWriteTime -Descending |
             Select-Object -First 1

if (-not $installer) {
    Write-Warning 'Installer EXE not found in output\ - check Inno Setup output above.'
    exit 1
}

Write-Host "`n=== Computing SHA-256 ===" -ForegroundColor Cyan
$sha256 = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash.ToLower()
Write-Host "  $sha256"

Write-Host "  (versions.json is updated automatically when you upload via Admin -> Releases)"

# -- Done ----------------------------------------------------------------------
Write-Host "`n=== Build complete ===" -ForegroundColor Green
Write-Host "Installer: $($installer.FullName)" -ForegroundColor Green
Write-Host "Size:      $([math]::Round($installer.Length / 1MB, 1)) MB"
Write-Host "SHA-256:   $sha256"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Upload $($installer.Name) via Admin -> Releases in the backend."
Write-Host "     The upload computes the SHA-256 and updates versions.json automatically."
Write-Host "  2. Running clients will auto-update within the hour."
