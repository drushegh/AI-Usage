#!/usr/bin/env pwsh
# Publish a Release build to ./dist and launch the tray. Run from projects/ai-usage/.
# The MSBuild CopyClaudeProbeOnPublish target stages ClaudeUsageProbe.exe next to the tray exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'

Write-Host "==> dotnet publish (Release) -> ./dist" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/AIUsageTray') -c Release -o $dist
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$exe = Join-Path $dist 'AIUsageTray.exe'
if (-not (Test-Path (Join-Path $dist 'ClaudeUsageProbe.exe'))) {
    throw "ClaudeUsageProbe.exe missing from dist — the Claude side would not work."
}
Write-Host "==> launching $exe" -ForegroundColor Green
Write-Host "    (look for the tray icon; Win11 may hide it in the overflow '^' flyout — pin it)" -ForegroundColor DarkGray
Start-Process $exe
