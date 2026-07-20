# AI-Usage — Installer

Per-user install (no administrator rights needed). Installs to
`%LOCALAPPDATA%\Programs\AIUsage`, adds a Start Menu shortcut, registers start-at-logon, and launches.

## Requirements
- **.NET 9 SDK** — the installer builds from source (`dotnet publish`).
- **.NET 9 Desktop Runtime** — to run the app (bundled with the SDK; the installer warns if it's missing).

## Install
```powershell
# from the Installer/ folder:
./install.ps1                 # publish + install + autostart + launch
./install.ps1 -NoAutoStart    # skip start-at-logon
./install.ps1 -NoLaunch       # install without launching
```
If PowerShell blocks the script, run it with:
`powershell -ExecutionPolicy Bypass -File .\install.ps1`

After install, the tray icon may land in the Windows 11 overflow (`^`) flyout — drag it onto the
taskbar to keep it glanceable.

## Uninstall
```powershell
./uninstall.ps1           # remove app + shortcut + autostart (keeps your config)
./uninstall.ps1 -Purge    # also delete %LOCALAPPDATA%\AIUsage (config + version cache)
```

## What it installs
- `%LOCALAPPDATA%\Programs\AIUsage\AIUsageTray.exe` (+ `ClaudeUsageProbe.exe`, the Claude helper) and their dependencies.
- Start Menu → **AI-Usage**.
- `HKCU\...\Run\AIUsage` (start-at-logon) unless `-NoAutoStart`.
- Config is written on first run to `%LOCALAPPDATA%\AIUsage\config.json`.

> Note: this is a framework-dependent build (small; needs the .NET 9 Desktop Runtime). A future
> self-contained or MSIX installer is on the roadmap (see `../PLAN.md`, T42).
