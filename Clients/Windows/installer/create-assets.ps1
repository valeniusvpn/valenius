<#
.SYNOPSIS
    Copies pre-built icon assets from /icons into the installer and TrayApp Resources.

.DESCRIPTION
    Outputs:
      installer\logo.ico              - copied from icons\app.ico
      installer\wizard-banner.png     - 164 x 314 px  (Inno Setup left panel)
      installer\wizard-small.png      -  55 x  58 px  (Inno Setup top-right)
      src\Valenius.TrayApp\Resources\logo.ico               - copy of app.ico
      src\Valenius.TrayApp\Resources\app.png                - copy of app.png
      src\Valenius.TrayApp\Resources\app-connected.ico      - copy of app-connected.ico
      src\Valenius.TrayApp\Resources\app-disconnected.ico   - copy of app-disconnected.ico

    Source files required in /icons:
      app.ico, app.png, app-connected.ico, app-disconnected.ico

    Run standalone:  .\create-assets.ps1
    Or called from:  build-installer.ps1
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$installerDir = $PSScriptRoot                                            # Clients\Windows\installer
$clientDir    = Split-Path $installerDir -Parent                         # Clients\Windows
$repoRoot     = Split-Path (Split-Path $clientDir -Parent) -Parent       # repo root (Valenius\)
$iconsDir     = Join-Path $repoRoot 'Shared\Images'
$resourcesDir = Join-Path $clientDir 'src\Valenius.TrayApp\Resources'

# --- Verify source files exist -------------------------------------------------

foreach ($f in @('app.ico', 'app.png', 'app-connected.ico', 'app-disconnected.ico')) {
    $p = Join-Path $iconsDir $f
    if (-not (Test-Path $p)) {
        Write-Error "Required icon file not found: $p"
        exit 1
    }
}

New-Item $resourcesDir -ItemType Directory -Force | Out-Null

# --- Helper: centred logo on white background for Inno Setup wizard -----------

function New-WizardImage {
    param([string]$SourcePath, [string]$DestPath, [int]$W, [int]$H, [float]$Scale = 0.80)

    $src = [System.Drawing.Image]::FromFile((Resolve-Path $SourcePath).Path)
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)

    $g.Clear([System.Drawing.Color]::White)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

    $ratio = [Math]::Min($W / $src.Width, $H / $src.Height) * $Scale
    $newW  = [int]($src.Width  * $ratio)
    $newH  = [int]($src.Height * $ratio)
    $x     = [int](($W - $newW) / 2)
    $y     = [int](($H - $newH) / 2)

    $g.DrawImage($src, $x, $y, $newW, $newH)
    $g.Dispose(); $src.Dispose()

    $bmp.Save($DestPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  Created: $DestPath"
}

# --- Copy installer assets ----------------------------------------------------

Write-Host "`nCopying installer assets ..." -ForegroundColor Cyan

$appIco  = Join-Path $iconsDir 'app.ico'
$appPng  = Join-Path $iconsDir 'app.png'

Copy-Item $appIco (Join-Path $installerDir 'logo.ico') -Force
Write-Host "  Copied: logo.ico"

New-WizardImage $appPng (Join-Path $installerDir 'wizard-banner.png') 164 314 0.78
New-WizardImage $appPng (Join-Path $installerDir 'wizard-small.png')   55  58 0.90

# --- Copy TrayApp resources ---------------------------------------------------

Write-Host "`nCopying TrayApp resources ..." -ForegroundColor Cyan

Copy-Item $appIco (Join-Path $resourcesDir 'logo.ico')         -Force; Write-Host "  Copied: logo.ico"
Copy-Item $appPng (Join-Path $resourcesDir 'app.png')          -Force; Write-Host "  Copied: app.png"
Copy-Item (Join-Path $iconsDir 'app-connected.ico')    (Join-Path $resourcesDir 'app-connected.ico')    -Force; Write-Host "  Copied: app-connected.ico"
Copy-Item (Join-Path $iconsDir 'app-disconnected.ico') (Join-Path $resourcesDir 'app-disconnected.ico') -Force; Write-Host "  Copied: app-disconnected.ico"

Write-Host "`nAssets ready." -ForegroundColor Green
