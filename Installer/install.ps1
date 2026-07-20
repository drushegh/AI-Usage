#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Per-user installer for the AI-Usage tray (no elevation required).
.DESCRIPTION
  Publishes a Release build from source and installs it to %LOCALAPPDATA%\Programs\AIUsage,
  creates a Start Menu shortcut, registers auto-start-at-logon (HKCU Run), and launches it.
  This matches the app's per-user, unpackaged deployment model (DESIGN.md §2).

  Requires: the .NET 9 SDK (to build) and the .NET 9 Desktop Runtime (to run). Both are the
  same install if you have the SDK.
.PARAMETER NoAutoStart
  Skip registering start-at-logon.
.PARAMETER NoLaunch
  Install without launching the app.
.EXAMPLE
  ./install.ps1
  ./install.ps1 -NoAutoStart
#>
[CmdletBinding()]
param(
    [switch] $NoAutoStart,
    [switch] $NoLaunch
)
$ErrorActionPreference = 'Stop'

$AppName    = 'AIUsage'
$ExeName    = 'AIUsageTray.exe'
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent $scriptDir          # Installer/ -> project root
$trayProj   = Join-Path $repoRoot 'src/AIUsageTray'
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"

Write-Host "AI-Usage installer (per-user, no elevation)" -ForegroundColor Cyan

# --- prerequisite check -----------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK ('dotnet') is not on PATH. Install the .NET 9 SDK, then re-run."
}

# --- publish a fresh Release build to a staging dir -------------------------
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "aiusage-install-$([System.IO.Path]::GetRandomFileName())"
Write-Host "==> Publishing (Release) ..." -ForegroundColor Cyan
dotnet publish $trayProj -c Release -o $staging | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $staging 'ClaudeUsageProbe.exe'))) {
    throw "Publish did not stage ClaudeUsageProbe.exe — the Claude side would not work. Aborting."
}

# --- stop any running instance ----------------------------------------------
Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "==> Stopping running instance (pid $($_.Id))"; $_ | Stop-Process -Force }
Start-Sleep -Milliseconds 300

# --- copy into the install dir ----------------------------------------------
Write-Host "==> Installing to $installDir" -ForegroundColor Cyan
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $installDir -Recurse -Force
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
$exePath = Join-Path $installDir $ExeName

# --- Start Menu shortcut ----------------------------------------------------
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = Join-Path $startMenu 'AI-Usage.lnk'
$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($lnk)
$sc.TargetPath = $exePath
$sc.WorkingDirectory = $installDir
$sc.Description = 'AI-Usage — combined Claude + Codex usage tray'
$sc.Save()
Write-Host "==> Start Menu shortcut created" -ForegroundColor DarkGray

# --- auto-start at logon (HKCU Run) -----------------------------------------
if (-not $NoAutoStart) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty -Path $runKey -Name $AppName -Value "`"$exePath`"" -PropertyType String -Force | Out-Null
    Write-Host "==> Registered start-at-logon" -ForegroundColor DarkGray
}

# --- runtime check + launch -------------------------------------------------
$hasDesktopRuntime = (dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 9\.') -ne $null
if (-not $hasDesktopRuntime) {
    Write-Warning "The .NET 9 Desktop Runtime was not detected. Install it from https://dotnet.microsoft.com/download/dotnet/9.0 (Desktop Runtime) or the app won't launch."
}

Write-Host "==> Installed: $exePath" -ForegroundColor Green
Write-Host "    Tip: Windows 11 hides new tray icons in the overflow ('^') flyout — drag it onto the taskbar to keep it visible." -ForegroundColor DarkGray
if (-not $NoLaunch) {
    Start-Process $exePath
    Write-Host "==> Launched." -ForegroundColor Green
}
Write-Host "    To remove: ./uninstall.ps1" -ForegroundColor DarkGray
