#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build the per-user AI-Usage .msi installer (self-contained, no admin, no .NET prerequisite).
.DESCRIPTION
  Repeatable end-to-end packaging:
    1. Publishes AIUsageTray self-contained (win-x64) into a staging folder.
    2. Publishes ClaudeUsageProbe self-contained into the SAME folder, on top of
       the framework-dependent copy the tray's build drops there — so BOTH exes
       carry the .NET 9 runtime and the target machine needs no .NET installed.
    3. Builds Installer/wix/AIUsage.Installer.wixproj (WiX v5 MSBuild SDK) into a
       single per-user .msi, and copies it to the Installer/ folder.

  Requires ONLY the .NET SDK (9.0.x). The WiX v5 SDK + UI extension restore from
  NuGet on first build; there is no WiX tool to install and no EULA to accept
  (WiX v5 predates the v6/v7 Open Source Maintenance Fee gate).
.PARAMETER Version
  Product version baked into the MSI (default 1.0.0). First three fields drive
  upgrade comparison; bump for each release.
.PARAMETER Runtime
  .NET runtime identifier for the self-contained payload (default win-x64).
.PARAMETER Configuration
  Build configuration (default Release).
.EXAMPLE
  ./build-msi.ps1
  ./build-msi.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string] $Version       = '1.0.4',
    [string] $Runtime       = 'win-x64',
    [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot     = Split-Path -Parent $installerDir           # Installer/ -> project root
$trayProj     = Join-Path $repoRoot 'src/AIUsageTray'
$probeProj    = Join-Path $repoRoot 'src/ClaudeUsageProbe'
$stage        = Join-Path $installerDir 'build/stage'
$wixProj      = Join-Path $installerDir 'wix/AIUsage.Installer.wixproj'
$msiName      = "AIUsage-$Version-$Runtime.msi"

Write-Host "AI-Usage MSI build (per-user, self-contained, $Runtime)" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK ('dotnet') is not on PATH. Install the .NET 9 SDK, then re-run."
}

# --- 1. clean stage --------------------------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# --- 2. publish the tray self-contained ------------------------------------
# NOTE: this also fires the csproj's CopyClaudeProbeOnPublish target, which drops
# the *framework-dependent* probe into $stage. Step 3 overwrites it with the
# self-contained probe, so ORDER MATTERS (tray first, probe second).
Write-Host "==> Publishing AIUsageTray (self-contained $Runtime) ..." -ForegroundColor Cyan
dotnet publish $trayProj -c $Configuration -r $Runtime --self-contained true -o $stage | Out-Host
if ($LASTEXITCODE -ne 0) { throw "tray publish failed ($LASTEXITCODE)" }

# --- 3. publish the probe self-contained INTO THE SAME folder --------------
Write-Host "==> Publishing ClaudeUsageProbe (self-contained $Runtime) ..." -ForegroundColor Cyan
dotnet publish $probeProj -c $Configuration -r $Runtime --self-contained true -o $stage | Out-Host
if ($LASTEXITCODE -ne 0) { throw "probe publish failed ($LASTEXITCODE)" }

# --- 4. verify both self-contained exes are staged -------------------------
foreach ($exe in @('AIUsageTray.exe','ClaudeUsageProbe.exe')) {
    if (-not (Test-Path (Join-Path $stage $exe))) {
        throw "Staging is missing $exe — the MSI would be incomplete. Aborting."
    }
}
# The probe's runtimeconfig must be self-contained (includedFrameworks), else the
# Claude helper would still need the .NET runtime on the target machine.
$probeCfg = Get-Content (Join-Path $stage 'ClaudeUsageProbe.runtimeconfig.json') -Raw
if ($probeCfg -notmatch 'includedFrameworks') {
    throw "ClaudeUsageProbe.runtimeconfig.json is not self-contained (no includedFrameworks). Aborting."
}
$stageSize = '{0:N0} MB' -f ((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "==> Staged self-contained payload: $stageSize" -ForegroundColor DarkGray

# --- 5. build the MSI ------------------------------------------------------
Write-Host "==> Building MSI (WiX v5) ..." -ForegroundColor Cyan
dotnet build $wixProj -c $Configuration `
    "-p:ProductVersion=$Version" `
    "-p:PayloadDir=$stage" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

$builtMsi = Join-Path $installerDir "wix/bin/$Configuration/$msiName"
if (-not (Test-Path $builtMsi)) { throw "Expected MSI not found at $builtMsi" }

# --- 6. drop the .msi into Installer/ --------------------------------------
$outMsi = Join-Path $installerDir $msiName
Copy-Item $builtMsi $outMsi -Force
$msiSize = '{0:N1} MB' -f ((Get-Item $outMsi).Length / 1MB)

Write-Host ""
Write-Host "==> Built: $outMsi  ($msiSize)" -ForegroundColor Green
Write-Host "    Per-user, no admin, self-contained. Double-click to install." -ForegroundColor DarkGray
Write-Host "    Unsigned: SmartScreen -> 'More info' -> 'Run anyway' (see README.md)." -ForegroundColor DarkGray
