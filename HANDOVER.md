# Usage Tray — Handover & Research Dossier

> **Created 2026-07-19** by the CCMAF dev session that also shipped the `/crossbench` command.
> This is a **self-contained** brief for building a **combined Claude + Codex usage system-tray app
> on Windows**. All research is embedded here (the raw findings live in another repo's gitignored
> workbench, so nothing is linked — this file stands alone). **Workflow:** read this → run a
> `/crossbench` on the brief in §7 to produce a consolidated design → build it.
>
> Everything below was **empirically verified** (actual local `~/.codex` and `~/.claude` files were
> inspected on this machine, plus ≥4 independent open-source implementations cross-checked) — not
> assumed from docs. Where something is undocumented or fragile, it says so plainly.

---

## 1. What we're building & why

A lightweight, always-on **Windows system-tray app** that shows, at a glance, current usage/limits
for **both** AI providers the user runs heavily:

- **Claude** (Claude Code in VS Code + the Claude desktop app, on a Claude subscription).
- **OpenAI** via the **codex CLI** (used for advisory "Sol/Terra/Luna" runs, image generation, and
  multi-advisor "crossbench" panels), on a **ChatGPT subscription** (OAuth, **not** an API key).

**The gap it fills:** Claude usage is visible in ~2 clicks in the Claude UI (5-hour + weekly limits),
but **codex/OpenAI usage has no easy view** — the user works "blind" on it. There is **no existing
combined Claude+Codex tray for Windows** (see §5). So we build one.

---

## 2. Hard rules (non-negotiable — owner-set)

1. **Never display an inaccurate number.** A wrong gauge is *worse* than none — it poisons trust in
   the accurate values beside it. Every figure is either the **real, authoritative value** or an
   explicit **"n/a"/unavailable** — never a stale, cached-past-expiry, or **heuristically-estimated**
   stand-in shown as real.
2. **Decision rule on the Claude side (already resolved — build it):** include Claude's real
   5h/weekly limit-% **only because it turned out to be reliably obtainable** (§4). Note the axis:
   the limit-% is *accurate* but comes from an **undocumented** endpoint — so the rule becomes
   **degrade to "n/a", never to an estimate**, if that endpoint ever changes/breaks. Token/cost
   aggregation is NOT a substitute for the limit-% (it's a different number the user doesn't check).
3. **This is a personal tool** — no company/confidential data involved. The one sensitivity is the
   local **OAuth credential** the Claude side reads (§4/§6): it must never leave the machine.

---

## 3. FIXED empirical facts — CODEX usage (the easy half: local, free, rock-solid / T1)

Every `codex` run appends to `~/.codex/sessions/**/*.jsonl`. Each turn ends with an `event_msg` of
`payload.type: "token_count"` carrying a **`rate_limits`** object. Confirmed on this machine across
22/22 advisor session files (codex-cli **0.144.1**). Shape:

- `primary.used_percent` — the utilization %.
- `window_minutes` — **300** = the 5-hour window, **10080** = the weekly window.
- `resets_at` — reset timestamp.
- `credits.balance` — remaining credits.
- `plan_type` — e.g. `"plus"`.

**Read the latest such event** → zero network, zero auth, zero ToS question. (Prior art doing exactly
this: `douglasmonsky/codex-usage-tracker`, `steipete/CodexBar`, `ccusage`.) **Caveat:** OpenAI
*removed the 5-hour Codex cap around mid-2026* — older sessions show both a 300-min and a 10080-min
window; recent ones show only the weekly (10080) window. **Handle windows that appear/disappear.**

---

## 4. FIXED empirical facts — CLAUDE usage (two mechanisms, very different reliability)

### (i) Token / cost aggregation — local, T1, rock-solid — but NOT the limit-%
`~/.claude/projects/**/*.jsonl` carries a per-message `usage` object (input/output/cache tokens);
`~/.claude/stats-cache.json` has daily/model rollups (tokens, costUSD, session counts). This is what
`ccusage` / `claude-monitor` read. **It structurally CANNOT produce the 5h/weekly limit-%** — that %
is a server-side, plan-weighted, rolling-window quota. ccusage-style "5-hour blocks" are a **local
heuristic reconstruction**, not the authoritative number. **Do not** present this as the limit-%.

### (ii) The real 5h/weekly limit-% — via ONE undocumented endpoint (T3/T4)
This IS obtainable and accurate, and it's what the Claude UI's `/usage` + Settings→Usage show:

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken from ~/.claude/.credentials.json>
User-Agent: claude-code/<version>        # REQUIRED — omit it → aggressively throttled 429s
```

- The OAuth token lives locally at `~/.claude/.credentials.json` (top-level `claudeAiOauth`:
  `accessToken`, `refreshToken`, `expiresAt`, `scopes`, `subscriptionType`, `rateLimitTier`,
  `organizationUuid`). `subscriptionType`/`rateLimitTier` are static plan classifiers — the **live**
  numbers only come from the endpoint above.
- **Response shape** (from independent OSS READMEs): `five_hour`, `seven_day`, `seven_day_opus`,
  `seven_day_sonnet` — each with `utilization` + `resets_at` — plus optional extra-usage / monthly-
  credit data. Exactly the numbers the UI shows, per-model-family, with reset timestamps.
- **Polling:** ≤ every **~180s** with exponential backoff (per a maintainer's guidance).
- **Reliability: T3/T4 — undocumented.** Absent from Anthropic's public rate-limit docs; two GitHub
  requests asking Anthropic to expose this officially were **closed without implementation**. It can
  change/break without notice, and it's **ToS-grey** (own account, read-only, personal; but a spoofed
  UA + an unofficial endpoint). ≥4 independent, actively-maintained OSS trays converge on this exact
  endpoint/auth/response-shape, so it's real signal — but **build a graceful-degradation path**: if it
  401s / 404s / changes shape, show **"n/a"** (per Rule 1 — never fall back to the local *estimate*).
- **Avoid** the alternative `claude.ai/api/organizations/<id>/usage` route (needs a session cookie;
  more Cloudflare/session-fragile). The OAuth endpoint is the less-fragile path.

---

## 5. Platform + the OSS landscape (verified — no combined Windows tray exists)

**Platform:** Windows 11 primary (cross-platform a bonus, not a requirement).

| Tool | Platform | Claude limit-% | Codex | Notes |
|---|---|---|---|---|
| **f-is-h/Usage4Claude** | **macOS only** | ✓ (5h/7d/Opus/Sonnet) | ✓ | The real combined tray — but Mac-only |
| **xikxp1/claude-monitor** | Rust/Tauri, cross-platform | ✓ | **✗** | Claude-only, and via the *fragile cookie* method (not the OAuth endpoint) |
| **projectvelox/claude-usage-widget** | **Windows** (Electron) | ✓ | roadmap | **Mature** (25+ releases, active CI/CD); uses the *correct* OAuth endpoint; Codex on roadmap, not shipped — **strong fork/head-start candidate** (license unconfirmed — check before forking) |
| **jens-duttke/usage-monitor-for-claude** | Windows | ✓ | ✗ | Windows, Claude-only |

**Bottom line:** the missing piece is the *easy* half (codex, §3). The pragmatic build path is likely
**fork `projectvelox/claude-usage-widget`** (Windows, mature, already does the hard Claude-OAuth half)
and add a codex panel reading `~/.codex/sessions/**/*.jsonl` — vs. building from scratch. Forking also
keeps the code that touches the OAuth credential under the owner's control (a real consideration).

---

## 6. The design decisions a crossbench should resolve

1. **Build strategy** — from scratch vs **fork/extend `projectvelox`** vs contribute Codex upstream.
   Weigh effort vs **control over the code touching the OAuth credential**, Electron footprint
   (~88 MB), maintenance. (Confirm projectvelox's license if forking.)
2. **Tech stack + footprint** — Electron vs Tauri/Rust vs Go vs Python+pystray vs .NET, for an
   always-on tray the owner trusts with the credential. Optimise footprint + auditability.
3. **Provider architecture + polling** — one abstraction over two sources with opposite reliability
   (codex: local/free/instant; claude: remote/undocumented/≤180s-throttled), extensible to a 3rd.
   Caching, backoff, 429/UA handling; one provider's failure must never degrade the other.
4. **The accuracy / graceful-degradation contract (Rule 1)** — guarantee "real value or explicit
   n/a, never a wrong number." When the Claude endpoint fails/changes → **n/a**, not the ccusage-style
   estimate. Staleness bounds (a value older than its own reset window → n/a); endpoint-shape-change
   detection; tell the user "unavailable — check the real UI", never silently blank.
5. **Credential + security posture** — token never leaves the machine; no telemetry/exfiltration;
   safe handling of the undocumented call; resilience to endpoint change; auditability/least-privilege.
6. **What's shown** — glanceable tray icon + tooltip vs detail popup: both providers' 5h + weekly,
   per-model (Opus/Sonnet) where available, reset countdowns, codex credits; icon signals "near limit".
7. **(bounded) Challenge the form** — is a tray the right surface, or a statusline/notification-on-
   threshold? One paragraph, a sanity-check not a redesign.

---

## 7. Ready-to-run crossbench brief

Run **`/crossbench`** (default roster `fable,sol` — a Claude seat + a codex seat) with the brief
below. `/crossbench` will Diverge (both advisors design independently) → Cross (each reviews the
other's) → Converge (a fresh Fable folds it into one buildable spec). **Advisor deps:** the project
needs codex configured + a `.claude/advisors.toml` + Fable available (same as `/consult`/`/sol`). Paste
this as the task:

```
Design the cleanest, most buildable approach for a combined Claude + Codex usage system-tray app on
Windows 11, and take a clear, reasoned position on each of the seven decisions below. It shows, at a
glance, current usage/limits for BOTH providers.

HARD RULE (owner, non-negotiable): never display an inaccurate number — every figure is the real
authoritative value or an explicit "n/a", never a stale or heuristically-estimated stand-in shown as
real. When a source fails, degrade to n/a, NOT to an estimate.

FIXED FACTS (empirically verified — design within these, do not relitigate):
- CODEX usage (easy/local/free/T1): ~/.codex/sessions/**/*.jsonl ends each turn with a token_count
  event carrying rate_limits { primary.used_percent, window_minutes (300=5h/10080=weekly), resets_at,
  credits.balance, plan_type }. Read the latest. (OpenAI removed the 5h codex cap ~mid-2026 → recent
  sessions may show only the weekly window; handle windows appearing/disappearing.)
- CLAUDE usage, two mechanisms: (i) token/cost is local/T1 (~/.claude/projects/**/*.jsonl usage +
  stats-cache.json) but CANNOT yield the 5h/weekly limit-%; (ii) the real limit-% comes from an
  UNDOCUMENTED endpoint GET https://api.anthropic.com/api/oauth/usage, Authorization: Bearer
  <accessToken from ~/.claude/.credentials.json>, User-Agent: claude-code/<version> (REQUIRED, else
  429), poll <=180s with backoff. Response: five_hour, seven_day, seven_day_opus, seven_day_sonnet
  (each utilization + resets_at) + optional extra-usage/credits. T3/T4 undocumented, ToS-grey,
  could break -> graceful-degrade to n/a.
- Platform: Windows 11 primary. Owner is security-conscious about the OAuth credential.
- OSS landscape: NO combined Claude+Codex tray for Windows. projectvelox/claude-usage-widget
  (Windows, Electron, mature, uses the correct OAuth endpoint, Claude-only, Codex on roadmap) is a
  fork candidate; xikxp1/claude-monitor (Tauri, cross-platform, Claude-only, cookie-based);
  f-is-h/Usage4Claude (macOS, combined); jens-duttke/usage-monitor-for-claude (Windows, Claude-only).

Take a clear, reasoned position on each:
1. Build strategy — from scratch vs fork/extend projectvelox vs contribute Codex upstream (weigh
   effort vs control over the code touching the OAuth credential, Electron footprint, maintenance;
   confirm projectvelox's license if forking).
2. Tech stack + footprint — Electron vs Tauri/Rust vs Go vs Python vs .NET for an always-on tray the
   owner trusts with the credential.
3. Provider architecture + polling — one abstraction over two opposite-reliability sources (codex
   local/free/instant; claude remote/undocumented/throttled), extensible to a 3rd; caching, backoff,
   429/UA handling, isolation so one provider's failure never degrades the other.
4. The accuracy / graceful-degradation contract — guarantee "real value or explicit n/a, never a
   wrong number." When the Claude endpoint fails/changes: n/a, not the ccusage-style estimate. Define
   staleness bounds, endpoint-shape-change detection, and how the user is told it's unavailable.
5. Credential + security posture — token never leaves the machine, no telemetry, safe handling of the
   undocumented call, resilience to endpoint change, auditability/least privilege.
6. What's shown — glanceable tray icon + tooltip vs detail popup: both providers' 5h + weekly,
   per-model (Opus/Sonnet) where available, reset countdowns, codex credits; icon "near-limit" signal.
7. (bounded, one paragraph) Challenge the form — tray vs statusline widget vs notification-on-
   threshold. A sanity-check, not a redesign.

Deliver: a concrete, buildable design (build strategy + stack, provider/polling architecture, the
accuracy/degradation contract, security posture, display) with your position + rationale on 1-7, and
a short "residual open questions to settle at build time" list.
```

---

## 8. Provenance & notes

- This dossier and the `/crossbench` command came out of a CCMAF dev session on 2026-07-19. The
  usage tray was scoped as a **standalone** utility (decoupled from a separate "media relay" tool the
  owner is building elsewhere — ignore that here; this project builds only the tray).
- The research was done by two evidence-gathering passes that **read the actual local session/credential
  files on this machine** and cross-checked ≥4 open-source trays — high confidence on the mechanisms,
  honest about the undocumented/fragile Claude-endpoint caveat.
- If any fact here reads as stale by the time you build (this is a fast-moving surface), **re-verify
  empirically** before designing around it — that discipline is exactly what turned "no reliable way
  to get Claude usage" into the §4(ii) finding.
