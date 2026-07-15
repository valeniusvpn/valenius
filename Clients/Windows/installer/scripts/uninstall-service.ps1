#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the Valenius Windows service.
    Called by the Inno Setup uninstaller before files are deleted.
#>

$ErrorActionPreference = 'SilentlyContinue'

$ServiceName = 'Valenius'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "$ServiceName service not found - nothing to remove."
    exit 0
}

if ($existing.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
}

Write-Host "Stopping Valenius.TrayApp processes..."
Get-Process -Name 'Valenius.TrayApp' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Stopping active WireGuard tunnel services..."
Get-Service -Name 'WireGuardTunnel$*' -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Service  -Name $_.Name -Force -ErrorAction SilentlyContinue
    & sc.exe delete $_.Name | Out-Null
}

Write-Host "Deleting $ServiceName service..."
& sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "$ServiceName service removed successfully."
}
else {
    Write-Warning "sc.exe delete returned $LASTEXITCODE - the service may need manual cleanup."
}
