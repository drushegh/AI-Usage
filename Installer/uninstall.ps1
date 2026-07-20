#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Uninstall the per-user AI-Usage tray.
.DESCRIPTION
  Stops the app, removes %LOCALAPPDATA%\Programs\AIUsage, the Start Menu shortcut, and the
  start-at-logon entry. Leaves the config file (%LOCALAPPDATA%\AIUsage\config.json) unless -Purge.
.PARAMETER Purge
  Also delete %LOCALAPPDATA%\AIUsage (config + version cache).
#>
[CmdletBinding()]
param([switch] $Purge)
$ErrorActionPreference = 'Stop'

$AppName    = 'AIUsage'
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$dataDir    = Join-Path $env:LOCALAPPDATA $AppName
$lnk        = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AI-Usage.lnk'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Write-Host "AI-Usage uninstaller" -ForegroundColor Cyan

Get-Process -Name 'AIUsageTray' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "==> Stopping running instance (pid $($_.Id))"; $_ | Stop-Process -Force }
Start-Sleep -Milliseconds 300

if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force; Write-Host "==> Removed $installDir" }
if (Test-Path $lnk)        { Remove-Item $lnk -Force;                  Write-Host "==> Removed Start Menu shortcut" }
if (Get-ItemProperty -Path $runKey -Name $AppName -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name $AppName -Force; Write-Host "==> Removed start-at-logon entry"
}
if ($Purge -and (Test-Path $dataDir)) { Remove-Item $dataDir -Recurse -Force; Write-Host "==> Purged $dataDir (config + cache)" }

Write-Host "==> Uninstalled." -ForegroundColor Green
