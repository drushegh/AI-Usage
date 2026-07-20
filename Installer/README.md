# AI-Usage — Installer

The primary installer is a **per-user `.msi`**: double-click, no administrator
rights, and no .NET prerequisite (the runtime is bundled). It installs to
`%LOCALAPPDATA%\Programs\AIUsage`, adds a Start Menu shortcut, and starts the
tray at logon.

The PowerShell scripts (`install.ps1` / `uninstall.ps1`) are kept as a
**developer / from-source** alternative — smaller, but they build from source and
need the .NET 9 SDK + Desktop Runtime installed. Most people should use the `.msi`.

---

## Install (the .msi)

1. Double-click **`AIUsage-<version>-win-x64.msi`**.
2. If Windows SmartScreen appears (see below), choose **More info -> Run anyway**.
3. Follow the short wizard (Welcome -> Install -> Finish).

That's it — no "Run as administrator", no .NET download. The tray launches at the
next logon (and you can start it now from the Start Menu).

> **SmartScreen (unsigned installer).** This `.msi` is **not code-signed**, so on
> a machine that hasn't seen it before Windows may show
> *"Windows protected your PC"* or *"Unknown publisher"*. That is expected for an
> unsigned app. Click **More info**, then **Run anyway**. (Signing would remove the
> prompt; it needs a code-signing certificate, which this personal build doesn't
> ship with.)

### What it installs
- `%LOCALAPPDATA%\Programs\AIUsage\AIUsageTray.exe` + `ClaudeUsageProbe.exe` (the
  Claude helper) and the bundled .NET 9 runtime — **both** exes are self-contained,
  so nothing else needs to be on the machine.
- Start Menu -> **AI-Usage**.
- `HKCU\...\CurrentVersion\Run\AIUsage` -> start-at-logon (on by default).
- Config is still written on first run to `%LOCALAPPDATA%\AIUsage\config.json`
  (the installer never touches it).

### Don't want start-at-logon?
Autostart is its own MSI feature, installed by default. To install **without** it:
```powershell
msiexec /i AIUsage-1.0.0-win-x64.msi REMOVE=AutoStart
```
(You can always toggle it later from the app's own settings.)

### Upgrade
Just run a newer `.msi` — a stable `UpgradeCode` means it removes the old version
and installs the new one in place (no need to uninstall first).

### Uninstall
**Settings -> Apps -> Installed apps -> AI-Usage -> Uninstall**, or:
```powershell
msiexec /x AIUsage-1.0.0-win-x64.msi
```
This removes the app, the shortcut, and the autostart entry. Your
`%LOCALAPPDATA%\AIUsage\config.json` is left in place.

After install, the tray icon may land in the Windows 11 overflow (`^`) flyout —
drag it onto the taskbar to keep it glanceable.

---

## Build the .msi from source

`build-msi.ps1` does the whole thing — publish (self-contained) + package — and
drops the `.msi` next to itself in `Installer/`.

```powershell
# from the Installer/ folder:
./build-msi.ps1                 # -> Installer/AIUsage-1.0.0-win-x64.msi
./build-msi.ps1 -Version 1.2.0  # set the product version
```

**Requirements: only the .NET 9 SDK.** The installer is authored with the
[WiX Toolset](https://wixtoolset.org/) **v5** MSBuild SDK
(`Installer/wix/AIUsage.Installer.wixproj`), which restores from NuGet on the
first build — there is no separate WiX tool to install, and (unlike WiX v6/v7)
**no "Open Source Maintenance Fee" EULA to accept**.

What the script does:
1. `dotnet publish` **AIUsageTray** self-contained (`win-x64`) into `build/stage`.
2. `dotnet publish` **ClaudeUsageProbe** self-contained into the same folder (so
   the Claude helper is runtime-independent too — the tray's build stages a
   *framework-dependent* probe first, and this overwrites it; order matters).
3. `dotnet build` the `.wixproj` -> a single per-user `.msi`.

To bump the version for a release, pass `-Version`, or edit `<ProductVersion>` in
`AIUsage.Installer.wixproj`.

### Optional: validate the .msi
The `dotnet build` above already runs Windows Installer (ICE) validation. To run
it standalone, use the WiX CLI pinned in this project's local tool manifest
(`.config/dotnet-tools.json` — WiX v5, so no v6/v7 EULA gate):
```powershell
dotnet tool restore
dotnet wix msi validate -sice ICE38 -sice ICE64 -sice ICE91 .\AIUsage-1.0.0-win-x64.msi
```
`ICE38`/`ICE64`/`ICE91` are suppressed because they are known false-positives for
a per-user install whose payload lives in the user profile (see the comments in
`Package.wxs`); every other ICE check passes clean.

---

## Developer alternative: install from source (PowerShell)

Builds a **framework-dependent** copy from source and installs it. Needs the
.NET 9 SDK (to build) and the .NET 9 Desktop Runtime (to run).

```powershell
./install.ps1                 # publish + install + autostart + launch
./install.ps1 -NoAutoStart    # skip start-at-logon
./install.ps1 -NoLaunch       # install without launching

./uninstall.ps1               # remove app + shortcut + autostart (keeps config)
./uninstall.ps1 -Purge        # also delete %LOCALAPPDATA%\AIUsage (config + cache)
```
If PowerShell blocks the script:
`powershell -ExecutionPolicy Bypass -File .\install.ps1`

Both installers use the same per-user location (`%LOCALAPPDATA%\Programs\AIUsage`),
so they're interchangeable — but don't run both at once.
