# SPIKE E2 — Claude Code version discovery for the AI-Usage tray

**Status:** complete · read-only investigation · **do not commit**
**Machine:** Windows 11 Pro (win32-x64), user `damie`, Node v22.18.0 / npm 10.9.3
**Installed Claude Code:** `2.1.191` · install method: **npm global** (`~/.claude.json` → `"installMethod": "global"`)
**Date:** 2026-07-19

Goal: the tray must send `User-Agent: claude-code/<version>` on the undocumented usage
endpoint (wrong/absent UA → 429). Resolve the version **robustly** (readable metadata
first, spawn as fallback), **cache last-known-good**, survive a PATH-less logon start,
and **only** report `n/a` if no version has *ever* been resolved. Never fabricate.

---

## 1. Where the `claude` executable lives on this machine

`claude` on PATH resolves to the **npm global shim**, not a binary:

```
command -v claude → /c/Users/damie/AppData/Roaming/npm/claude
```

`%APPDATA%\npm` (= `C:\Users\damie\AppData\Roaming\npm`) is npm's global-bin dir. It holds
three generated shims (one per shell), all created 2025-06-24:

| Shim | Consumer | What it does |
|---|---|---|
| `claude` (no ext) | Git Bash / MSYS | `exec .../bin/claude.exe "$@"` |
| `claude.cmd` | cmd.exe | `"%dp0%\...\bin\claude.exe" %*` |
| `claude.ps1` | PowerShell | `& "$basedir/.../bin/claude.exe" $args` |

Every shim execs the **same real target** — a native, self-contained executable:

```
C:\Users\damie\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe
```

Key fact: this is **NOT an npm/node JS script**. It is a ~220 MB native Windows binary
(`FileDescription: Claude Code`, `ProductName: Claude Code`). The npm package now ships a
platform-specific native binary (copied from the `@anthropic-ai/claude-code-win32-x64`
optionalDependency by `install.cjs` postinstall). So "installed via npm" ≠ "runs via node".

There is **no** separate native-installer binary on this machine — the `.local/bin` and
`%LOCALAPPDATA%\Programs\claude-code` locations are **absent** (confirmed by probe). npm-global
is the sole install here.

Package layout (parent of the exe):
```
%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\
  package.json        ← readable version metadata (see §2)
  bin\claude.exe       ← native binary; Windows FileVersion metadata (see §2)
  install.cjs  cli-wrapper.cjs  sdk-tools.d.ts  README.md  LICENSE.md
```

---

## 2. Readable version metadata — NO process spawn (preferred path)

Two solid, spawn-free sources exist on this machine. Both yield exactly `2.1.191`
(matches `claude --version`).

### 2a. `package.json` `version` field — CANONICAL, recommended primary
```
Path : %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\package.json
Read : JSON parse → .version  →  "2.1.191"
```
- Exactly the 3-part semver the UA token needs (`claude-code/2.1.191`). No trimming.
- Tiny (1.5 KB), deterministic, updated in lockstep with the binary on every npm update.
- `.NET`: `JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("version").GetString()`.

### 2b. `claude.exe` Windows FileVersion — spawn-free, install-method-agnostic
```
Path : %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe
FileVersion    : 2.1.191.0
ProductVersion : 2.1.191.0
ProductName    : Claude Code
```
- `.NET`: `FileVersionInfo.GetVersionInfo(exePath).ProductVersion` → `"2.1.191.0"`.
- **4-part** — trim the trailing `.0` to get the 3-part UA token `2.1.191`.
- Advantage: works for **any** install shape that produces a `claude.exe` (npm-global *or*
  native installer), because it reads the binary's own embedded metadata rather than a
  sibling package.json. Good universal fallback when 2a's path is absent.

### Do NOT use for the version: `~/.claude/.last-update-result.json`
It exists and *looks* tempting (`version_from` / `version_to`), but it is an **update-attempt
log, not a current-version record**:
```json
{"outcome":"failed","status":"install_failed","version_from":"2.1.186",
 "version_to":"2.1.191","error_code":"update_apply_exe_locked"}
```
Here it records a **failed** update to 2.1.191 — yet the machine *is* running 2.1.191 (a later
attempt succeeded without rewriting this file). It can be stale, reflect a failed/rolled-back
attempt, or name a version that never became live. Never trust it as the version source.

Also do not write into `~/.claude/` at all — that tree is Claude Code's own; the tray keeps
its cache in its own app dir (§4).

---

## 3. PATH-less / minimal-environment logon startup

The design's hard case: the tray autostarts at Windows logon in a **minimal environment**
where the user PATH may not be fully populated, so bare `claude` may not resolve.

What survives, and what to hard-code:

| Resolution method | Survives minimal logon? | Notes |
|---|---|---|
| Bare `claude` on PATH (`command -v`, or `Process.Start("claude")`) | ✗ unreliable | npm global bin (`%APPDATA%\npm`) is on the **user** PATH; a stripped/minimal logon env may not have it. Don't depend on it. |
| **`%APPDATA%`-anchored absolute path** | ✓ reliable | `%APPDATA%` (Roaming) is a core per-user env var the OS sets for the logon session itself — present even when PATH is minimal. In .NET: `Environment.GetFolderPath(SpecialFolder.ApplicationData)`. **Confirmed** it resolves to `C:\Users\damie\AppData\Roaming` and the package dir + package.json + claude.exe all exist under it. |
| `%LOCALAPPDATA%`-anchored (native-installer path) | ✓ reliable *if present* | Same env-var reliability; but the native-installer dirs are **absent on this machine**. Probe-if-exists only. |
| Spawning `claude --version` | ✓ *only if* you give it the absolute exe path + a timeout | Works, but it is the fragile path (process launch, disk not yet warm at logon, exe-locked during an in-progress self-update). Fallback, not primary. |

**Stable absolute paths to hard-code (anchored on env vars, not literal `C:\Users\damie`):**

```
Base (npm-global):  %APPDATA%\npm\node_modules\@anthropic-ai\claude-code
  → \package.json          (primary metadata, §2a)
  → \bin\claude.exe         (FileVersion metadata §2b; spawn target for §2 fallback)

Probe-if-exists (native installer — NOT on this machine, present on others):
  %LOCALAPPDATA%\Programs\claude-code\claude.exe
  %USERPROFILE%\.local\bin\claude.exe
```

Build these with `Environment.GetFolderPath(...)` / `Environment.ExpandEnvironmentVariables(...)`
— never a literal user profile string.

Timing (justifies the spawn timeout): warm `claude --version` measured **≈480–540 ms** on this
box (3 runs: 526/479/541 ms). A cold logon start (disk cache empty, 220 MB image) will be
slower. Budget a **hard timeout of ~3–5 s** and kill on expiry.

---

## 4. Recommended resolution order + cache design (implementable)

### Resolution order (fastest/most-reliable first)

```
ResolveVersion():
  1. VALIDATED INSTALL METADATA  (no process spawn — preferred)
     1a. Read %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\package.json
         → JSON .version → validate matches ^\d+\.\d+\.\d+$  → USE IT.
     1b. Else FileVersionInfo.GetVersionInfo(<claude.exe>).ProductVersion
         for each existing exe path (npm-global exe, then native-installer probes),
         trim trailing ".0" → validate x.y.z → USE IT.
     → On success: write-through to the cache (§ cache), return version.

  2. TIME-LIMITED  claude --version   (fragile fallback — only if step 1 found nothing)
     - Spawn the ABSOLUTE exe path if known (from step-1 probe list); else "claude"
       only if PATH resolution succeeds.
     - Hard timeout ~3–5 s; kill on timeout; redirect stdout.
     - Parse leading semver from stdout "2.1.191 (Claude Code)"  → regex ^(\d+\.\d+\.\d+).
     → On success: write-through to the cache, return version.

  3. LAST-KNOWN-GOOD CACHE  (only if 1 AND 2 failed — e.g. minimal logon, disk not ready)
     - Read the cache file; if it holds a valid version, return it.

  4. n/a  — ONLY if the cache has NEVER held a value. Never fabricate a number.
```

Notes:
- "Validated" = the path exists **and** the value matches `^\d+\.\d+\.\d+$`. A malformed read
  is treated as a miss, not a value.
- The UA token is always the 3-part semver: `User-Agent: claude-code/2.1.191`.
- Pass the resolved version to the probe **via argv** (per design) — resolve once per the
  cadence below, not per request.

### Persistent last-known-good cache

Store in the **tray app's own** per-user data dir (never `~/.claude`):
```
%LOCALAPPDATA%\<TrayAppName>\claude-version.json
  .NET: Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData),
                     "<TrayAppName>", "claude-version.json")
```
`%LOCALAPPDATA%` chosen over `%APPDATA%` (Roaming) so a machine-specific installed-version
cache doesn't roam to a different PC with a different Claude Code install.

Suggested contents:
```json
{
  "version": "2.1.191",
  "source": "package.json",          // package.json | fileversion | cli
  "resolvedAt": "2026-07-19T20:40:00Z",
  "sourcePath": "C:\\Users\\damie\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code\\package.json",
  "sourceMtimeUtc": "2026-06-25T00:00:00Z"  // for cheap change-detection (below)
}
```

### When to re-resolve (opportunistic refresh + write-through)
- **On startup:** run the full order once; refresh the cache if the live value changed.
- **On source change (catches Claude Code auto-updates cheaply, no spawn):** stat the
  metadata source (`package.json` — or the exe if using 2b) and compare its
  last-write-time to `sourceMtimeUtc`; re-run step 1 when it moved. Optionally a
  `FileSystemWatcher` on the package dir.
- **Daily backstop:** a once-a-day timer re-runs the order so a long-lived tray process
  eventually picks up a version bump even if the mtime check was missed.
- **Every successful live resolution (step 1 or 2) writes through** to the cache, so the
  last-known-good is always the newest value actually observed.

---

## Bottom line for the developer
- Primary: parse `version` from `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\package.json`.
- Universal spawn-free fallback: `FileVersionInfo` `ProductVersion` of `...\bin\claude.exe` (trim `.0`).
- Last resort live: time-limited `claude --version` against the **absolute** exe path (~3–5 s timeout).
- Persist last-known-good in `%LOCALAPPDATA%\<TrayApp>\claude-version.json`; re-resolve on
  startup, on source-mtime change, and daily.
- `%APPDATA%`/`%LOCALAPPDATA%` (via `SpecialFolder`) are the reliable anchors at logon; bare
  `claude` on PATH is not. `n/a` only when the cache has never held a value; never fabricate.
