# AI-Usage Tray — build notes

Combined Claude + Codex usage system-tray app. This `src/` tree is the buildable
solution root (in the eventual standalone repo it is the repo root). Design of
record: `../DESIGN.md`; task plan: `../PLAN.md`. Task **T1** = this scaffold: the
empty tray app boots (build green, tests green). No providers, no polling yet.

## TODO(net10) — target framework is temporarily .NET 9, not .NET 10

`DESIGN.md §2` settles on **.NET 10 LTS**. This machine currently has only the
**.NET 9 SDK (9.0.304)** installed — **the .NET 10 SDK is NOT present** — so the
scaffold targets `net9.0-windows` and `global.json` pins `9.0.304`, which lets the
build and tests actually verify today rather than fail SDK resolution.

When the .NET 10 SDK is installed, moving to .NET 10 is two trivial one-line edits
(no code changes — nothing in this skeleton is .NET-9-specific):

1. `global.json` → `"version": "<installed 10.x>"`.
2. Every `.csproj` `<TargetFramework>` → `net9.0-windows` → `net10.0-windows`
   (`AIUsageTray/AIUsageTray.csproj` and `AIUsageTray.Tests/AIUsageTray.Tests.csproj`).

The same note lives in `Directory.Build.props` and each `.csproj` comment.

## Layout

```
src/
├─ global.json                 SDK pin (9.0.304, rollForward latestFeature)
├─ Directory.Build.props       Nullable, TreatWarningsAsErrors, LangVersion latest, ImplicitUsings
├─ AIUsage.sln
├─ AIUsageTray/                WinExe — WPF Application (no window) + WinForms NotifyIcon
│  ├─ Program.cs               [STAThread] Main
│  ├─ App.cs                   Application subclass, OnExplicitShutdown, owns the tray
│  ├─ TrayIconController.cs    NotifyIcon + generated 16x16 icon + Exit menu item
│  ├─ NativeMethods.cs         DestroyIcon P/Invoke (LibraryImport) — frees the GDI icon handle
│  └─ AppInfo.cs               AppInfo.Name = "AI-Usage"
└─ AIUsageTray.Tests/          xUnit — one trivial passing test
   └─ AppInfoTests.cs
```

## Build / test

```
cd src
dotnet build
dotnet test
```

`TreatWarningsAsErrors` is on: warnings are defects, fixed properly, never suppressed.

## Runtime packages

**Zero third-party runtime NuGet packages.** The tray is BCL + WPF + WinForms only.
`CommunityToolkit.Mvvm` is permitted later in the UI layer only (`DESIGN.md §2`) — not
needed for the boot skeleton. Test-only packages (`xUnit`, `Microsoft.NET.Test.Sdk`)
live in `AIUsageTray.Tests` and never reach the tray.
