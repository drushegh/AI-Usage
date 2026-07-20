#!/usr/bin/env pwsh
# Build + test the AI-Usage tray. Run from projects/ai-usage/.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln  = Join-Path $root 'src/AIUsage.sln'

Write-Host "==> dotnet build (Release)" -ForegroundColor Cyan
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

Write-Host "==> dotnet test" -ForegroundColor Cyan
dotnet test $sln -c Release
if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }

Write-Host "==> OK — build + tests green" -ForegroundColor Green
