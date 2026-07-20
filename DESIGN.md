# AI-Usage Tray — Consolidated Final Design (Converge stage)

This is the single buildable spec folded from the Stage-1 Fable and Sol designs and both Stage-2
cross-reviews. Positions the cross round converged on are stated as **SETTLED** and are not re-argued.
Exactly one disagreement survived the cross round (the tray-icon form, §9); everything else below is
the design the build proceeds from.

The owner's HARD RULE governs everything: **every displayed figure is the real authoritative value or
an explicit "n/a" — never a stale or estimated stand-in shown as current.** There is no estimator,
interpolator, projector, or carry-forward anywhere in the codebase, not behind any flag. The cheapest
way to never show an estimated number is to be structurally unable to produce one.

---

> ## ⚠ SPIKE CORRECTIONS (2026-07-19) — these OVERRIDE the assumptions below
> Empirical spikes E1/E2/E3 ran against real files/endpoint on the owner's machine. Full evidence:
> `spikes/e1-claude-payloads/e1-findings.md`, `spikes/e2-version-discovery/e2-findings.md`,
> `spikes/e3-codex-sessions/e3-findings.md`. The build MUST use these corrected facts:
>
> **Claude endpoint (E1 — HTTP 200 captured, fixture `spikes/e1-claude-payloads/raw-capture-1.json`):**
> - **Utilization scale = 0–100 float** (`five_hour.utilization=28.0`); `limits[].percent` = int. Ambiguity resolved.
> - **`resets_at` = ISO-8601 with tz offset** (`DateTimeOffset`). Money = minor-units (`amount_minor`/`exponent`).
> - **Render primarily from the `limits[]` array** — `{kind: session|weekly_all|weekly_scoped, group, percent,
>   severity, resets_at, scope, is_active}`. **Per-model weekly is `weekly_scoped` + `scope.model.display_name`**,
>   NOT the top-level `seven_day_opus`/`seven_day_sonnet` (those exist but are `null` here). Server also gives `severity`.
> - The response carries **many null codename buckets** → "tolerate unknown/extra fields, never reject the envelope
>   on them" (§5) is now PROVEN mandatory. Drift = a *required* field (`limits[].percent`/`resets_at`,
>   `five_hour.utilization`) missing/typed-wrong/out-of-range → that metric n/a.
>
> **Codex sessions (E3 — verified vs 72 real files, fixtures in `spikes/e3-codex-sessions/`):**
> - Event is **`type:"event_msg"` with `payload.type=="token_count"`** (NOT top-level `token_count`).
>   Paths: `.payload.rate_limits.primary.{used_percent,window_minutes,resets_at}`, `.credits.balance` (string/null),
>   `.plan_type`. **`resets_at` = unix epoch SECONDS** (older files: relative `.resets_in_seconds`) — handle both.
> - **Per-line `.timestamp` (UTC ISO-8601 `Z`) CONFIRMED** → select newest event by it; **the §4.1 mtime-upper-bound
>   fallback is UNNEEDED**. (mtime/filename are LOCAL, embedded is UTC — never compare directly.)
> - **`primary` is NOT always the 5h window** (now weekly-only since ~2026-07-15; primary=weekly). Classify by
>   `window_minutes` (299/300≈5h, 10079/10080≈weekly, by proximity), NEVER by position.
> - Collector must: pick newest event with **non-null `primary`** (skip degenerate null-primary tails), **fall through
>   to older files** when a session has zero token_count events, and **parse line-by-line with try/catch** (partial
>   last line) — never slurp the tail.
>
> **Claude Code version for the UA (E2):** read `@anthropic-ai/claude-code/package.json` `.version` (spawn-free,
> canonical) → exe `FileVersion` fallback → timed `claude --version` (3–5s cap) → last-known-good cache in
> `%LOCALAPPDATA%`. Anchor paths on `%APPDATA%`/`%LOCALAPPDATA%` (minimal-logon safe). Do NOT read
> `.last-update-result.json` (update-attempt log, not the running version).
>
> **Build TFM:** targeting `net9.0-windows` for now (.NET 10 SDK not installed on the machine) — trivial bump when it lands.
>
> **Claude opt-in default (owner-ratified 2026-07-20):** §4.2 below reads "opt-in, default OFF". The OWNER has
> ratified **default ON** — this is a single-owner tool on the owner's own account, so the Claude side is enabled
> out of the box; the undocumented/ToS-grey caveat becomes a one-time README/first-run note, and the tray menu +
> Settings still let the owner turn it off. Where §4.2 / `ClaudeProvider` docs say "defaults OFF", read "defaults ON".

## 1. Build strategy — SETTLED: from scratch

**Build from scratch in C#/.NET.** Both Stage-1 designs independently reached this and both
cross-reviews endorsed it. The app is fundamentally small (two collectors, a scheduler, a typed state
model, a tray UI — roughly 2–3k lines); forking projectvelox would inherit Electron's footprint and
npm dependency tree around the credential, and the accuracy contract must live in the domain model,
not be retrofitted into someone else's display pipeline.

- **projectvelox is behavioural research, not a codebase.** Reading publicly available source to learn
  endpoint behaviour is fine regardless of licence (licence law constrains copying/modification/
  redistribution, not reading — cross-settled). Confirm the exact licence at the exact commit before
  copying any code, fixtures, icons, or assets; no licence on file = all-rights-reserved.
- Contributing Codex support upstream is optional later goodwill, never the delivery vehicle.

## 2. Stack, footprint, deployment — SETTLED

**SETTLED (a): .NET 10 LTS, one process, `UseWPF` + `UseWindowsForms` — a WPF `Window` for the popup
card, WinForms `NotifyIcon` for the tray. No third-party tray package, no WebView, no browser engine.**
Why: `NotifyIcon` is the mature dependency-free tray primitive; WPF gives the popup per-monitor-v2
DPI, UI Automation peers (Narrator), keyboard navigation, and `DynamicResource` theming for free —
the owner-drawn WinForms alternative hand-rolls all three and ships a popup that is blurry on mixed
DPI and silent in screen readers. Mixing the two frameworks in one process is standard and adds no
dependencies.

**Two executables:**

- `AIUsageTray.exe` — UI, scheduling, Codex collection/parsing, sanitized state only. Never reads the
  credential, never makes network calls (externally enforceable — see §6).
- `ClaudeUsageProbe.exe` — SETTLED (b): a short-lived process that reads `~/.claude/.credentials.json`,
  makes the one request, emits schema-validated usage over redirected stdout, and exits. Conditions
  (all part of the settled position): this is **not an OS security boundary** (same user; anything
  running as the user can read the credentials file directly — do not oversell it); the Claude Code
  version is resolved and cached in the tray and passed to the probe **via argv** (the version string
  is not a secret); probe stdout is **untrusted input** — the tray schema-validates it strictly;
  the probe is **excluded from WER/LocalDumps** minidump collection (and .NET `DbgEnableMiniDump`
  stays off) so a probe crash can never persist the token; the probe is launched by **absolute
  install-dir path**, no shell execution, launches serialized (at most one in flight).

**Runtime packages:** zero third-party NuGet on any code path that can see the token; the probe is
BCL-only (`HttpClient`, `System.Text.Json`) and small enough for line-by-line audit. In the tray,
BCL-first; `CommunityToolkit.Mvvm` is the only permissible addition and only in the UI layer.

**Deployment (resolves the impossible triad):** per-user install + non-user-writable binaries +
no-elevation cannot all hold. **Pick: per-user unpackaged install under `%LOCALAPPDATA%\Programs\`,
no elevation, user-writable binaries.** The dropped property costs almost nothing: there is no
intra-user boundary anyway — same-user malware can read `.credentials.json` directly, so
tamper-proofing the binaries buys nothing against the realistic attacker. No MSIX (filesystem
virtualisation is pure downside for an app whose job is reading two dot-directories).
Framework-dependent build if the .NET 10 runtime is present (it is, on the target machine),
self-contained otherwise. Footprint targets (informational, measured at build time, not
architecture-gating): idle working set ≤ ~40 MB framework-dependent, ~0% idle CPU.

**Persistence:** persist only rate-control metadata (last attempt, next eligible attempt), the
last-known-good Claude Code version string, and the user's config (thresholds, toggles — never
secrets). **Never persist usage responses to disk.** (Codex "dated" data needs no persistence — the
source JSONL files are the persistence.)

## 3. Data model — tri-state, per-window, plus a separate last-known record

Folded position (adopted by both sides in the cross round): the tri-state metric model, pushed down to
**per-window status and observation time** — one snapshot-level status/DataAsOf cannot express windows
that are independently live, missing, malformed, or expired — plus a **separate retained last-good
record** feeding the DATED area (a snapshot replacement must not destroy true history).

```csharp
enum MetricState { Available, NotApplicable, Unavailable }
// No null, no default zero, no retained-previous-value. Ever.

sealed record Metric<T>(
    MetricState State,
    T? Value,                       // present iff Available
    DateTimeOffset? ObservedAt,     // present iff Available — per-metric, not per-snapshot
    string? ReasonCode);            // present iff NotApplicable/Unavailable
                                    // e.g. "not-reported", "throttled", "auth-rejected",
                                    //      "source-changed", "no-recent-event", "scale-unpinned"

sealed record UsageWindow(
    int WindowMinutes,              // AUTHORITATIVE identity: 300, 10080, ... Preserved verbatim.
                                    // Never identify windows by array position or label.
    string Label,                   // derived for display: "5h", "Weekly", generic "Nh"
    Metric<decimal> UsedPercent,    // source precision preserved (decimal, unrounded)
    Metric<DateTimeOffset> ResetsAt);

enum SourceStatus { Ok, Unavailable }

sealed record ProviderSnapshot(
    string ProviderId,              // "claude", "codex"
    DateTimeOffset FetchedAt,
    SourceStatus Status,
    string? StatusReasonCode,
    IReadOnlyList<UsageWindow> Windows,   // whatever windows exist right now (appear/disappear = non-event)
    Metric<decimal> CreditsBalance,
    Metric<string> PlanType);

// Separate store; survives snapshot replacement; feeds ONLY the DATED "last known" area (§5).
sealed record LastKnownReading(
    string ProviderId,
    int WindowMinutes,
    decimal UsedPercent,            // unrounded, as observed
    DateTimeOffset ObservedAt,
    DateTimeOffset ResetsAtAtObservation);
```

Rules baked into the model:

- Missing 5h Codex window (post mid-2026 removal) = `NotApplicable("not-reported")` — never zero,
  never an error affecting the weekly value.
- A reset timestamp passing does **not** mean usage became zero — the window's metrics become
  `Unavailable` until a fresh authoritative observation arrives.
- Missing credits / plan / timestamps / malformed individual fields degrade **independently** to
  their own non-Available state; they never poison sibling metrics.
- A third provider is a new compile-time adapter + one registration line. No plugin loading into a
  process that displays trusted usage data.

## 4. Provider architecture + polling

Two isolated async loops publish immutable snapshots into a store; the UI renders only from the store.
Each loop has its own worker, exception boundary (top-level catch can only produce an `Unavailable`
snapshot), state slot, and cancellation. Loops share nothing but the store; store swap is atomic;
change events are marshalled to the WPF dispatcher. One provider's outage, hang (bounded by timeout),
or parse failure cannot delay or degrade the other. On startup each provider shows n/a until its first
observation; on resume from sleep, expired observations are invalidated **before** refreshes start.

### 4.1 Codex loop (local, free, event-driven)

- Resolve the sessions dir honouring `CODEX_HOME`; absent dir → `Unavailable("no-sessions-dir")`.
- `FileSystemWatcher` (recursive, `*.jsonl`, debounced ~2s) as the latency optimisation **plus** a
  60s reconciliation poll as the guarantee (watchers lose events).
- **Event selection (folded correction):** enumerate a bounded candidate set (newest ~10 files by
  mtime, ~14-day age cap, bounds configurable and observable), read bounded tails (~64 KB), parse
  lines back-to-front, and select the **greatest VALID embedded event timestamp across the whole
  candidate set**. File mtime only narrows candidates — it never determines truth (mtimes reorder
  under concurrent sessions, copying, antivirus). Tolerate: a partial UTF-8 tail line (discard it),
  sharing violations and mid-write files (open `FileShare.ReadWrite | Delete`, retry next cycle),
  per-file parse errors (skip that file, not the provider).
- **Pre-defined fallback** if a Codex version omits per-line timestamps (verify against real files
  first — empirical question E3): use event order within the file with file mtime as an *upper bound*
  on observation time, and admit that reading **only to the DATED area, never LIVE** — an
  mtime-anchored time is an explicitly weaker observation, not a freshness anchor.
- Map every window present by `window_minutes` (300 → "5h", 10080 → "Weekly", anything else → generic
  label — additive tolerance), plus `credits.balance` and `plan_type`.
- Zero network I/O on this path, ever.

### 4.2 Claude loop (remote, undocumented, throttled) — SETTLED (f) cadence

- **One request per 180 seconds, hard gate, all triggers coalesced** — timer, manual refresh, app
  restart, network restoration, resume, credential-file change all funnel through the same gate; none
  bypasses it. (180s is the only cadence valid under both readings of the "poll <=180s" fixed fact,
  and polling an endpoint whose every failure mode is total n/a faster yields *less* availability.)
- **Transient failures:** explicit backoff ladder **3 / 6 / 12 / 24 / 48 minutes, capped at 60** —
  easier to test and audit than a formula.
- **429:** honour a valid `Retry-After` when larger than the ladder step. **Folded correction:** a
  429 can mean UA-rejection, not throttle — after **5 consecutive 429s at compliant cadence**, stop
  presenting "throttled" and surface a distinct diagnostic state: `Unavailable("endpoint-or-UA-
  changed")` with a one-time notification, at the 60-min cap cadence. A UA-rejection never clears by
  waiting; an endless "throttled" loop is a lie.
- **401/403:** stop polling. One documented exception to trigger-coalescing: a single 401 may trigger
  one immediate credentials re-read + one retry *within the same logical attempt* (Claude Code may
  have just rotated the token). Still rejected → `Unavailable("auth-rejected", hint: "open Claude
  Code to refresh sign-in")` and wait for a credential change or a deliberate manual retry.
  **Credential re-arm (folded correction):** watch the credentials file's *parent directory* for
  create/change/rename (atomic replacement invalidates a file-attached watcher; the file may not
  exist at startup), with a periodic stat as fallback; all retries serialized through the 180s gate.
- **HTTP posture:** `AllowAutoRedirect = false`; **any 3xx is a protocol failure → n/a** ("the
  endpoint moved" is schema drift, not something to follow with a bearer token). Do not depend on
  the runtime's Authorization-stripping-on-redirect — an explicit off switch is testable, the runtime
  default is folklore. TLS ≥ 1.2, default certificate validation (never relaxed), 15s timeout,
  hard-coded origin `https://api.anthropic.com`, path `/api/oauth/usage`. **No certificate pinning**
  (brittle against a third party's cert rotation; the exact-origin/no-redirect policy is the control).
- **UA/version discovery (folded correction — must NOT fail closed into permanent outage):** resolve
  the installed Claude Code version **once in the tray** — validated install metadata first, else a
  time-limited `claude --version` — **cache last-known-good persistently**, re-resolve
  opportunistically (on startup, on version-file change, daily). Pass to the probe via argv. Claude
  is `n/a("no-version")` **only if no version has ever been resolved on this machine**. Never
  fabricate a version; a cached previously-real one is truthful ("the version this install reported"),
  a made-up one is not. Rationale: the HARD RULE governs displayed numbers, not HTTP headers — but a
  PATH-less startup environment must not brick the provider forever. Send exactly
  `claude-code/<version>`; never spoof or rotate UAs in response to 429s.
- **Schema validation:** strict DTOs; parse `five_hour`, `seven_day`, `seven_day_opus`,
  `seven_day_sonnet` independently (each utilization + resets_at), plus optional credit fields.
  Unknown *additional* fields are tolerated; record a non-sensitive field-name/type schema signature
  when new names/types appear. A known field disappearing, changing type, violating its established
  range, or becoming ambiguous → that metric `n/a`; untrustable envelope → all Claude metrics `n/a`
  (`"source-changed"`), one-time notification, backoff to cap. **Never log response bodies** —
  signature only.
- Opt-in and kill switch: the Claude remote provider is explicit opt-in (undocumented, ToS-grey), can
  be paused/disabled at any time; disabled → Claude limit fields read n/a. No silent fallback to
  cookies, browser automation, or estimates. The local `~/.claude` token/cost files are **never**
  converted into limit-% (they cannot yield it; the HARD RULE forbids a stand-in) — they may appear
  only as a separately-labelled "local activity" section, if ever.

## 5. The accuracy / degradation contract — LIVE, DATED, N-A

Every rendered figure is a validated, timestamped observation in exactly one of three states.
**A current field is either a fresh authoritative observation or n/a.** There is no fourth state.

### Prerequisite before any Claude number renders (folded correction — this gates the build)

The utilization **scale cannot be inferred by range sanity-check** — `0.42` is a legal value under
both the "fraction" and "percent" readings. The scale and units are pinned from **real captured
payloads committed as versioned fixtures** (empirical question E1) **before** any Claude number is
displayed; the parser hard-codes the pinned semantics with range checks. Ambiguity at runtime →
`Unavailable("scale-unpinned")`, never heuristic multiplication. **Rounding is part of the contract:**
preserve source precision, or apply an honest fixed display rule (one decimal place); threshold
comparisons always use the unrounded value.

### LIVE — plain display; the only state allowed in the tray icon and tooltip

A window metric is LIVE iff **all** hold:

1. Snapshot `Ok`, metric `Available`, schema-validated against the pinned semantics.
2. **Claude:** `now − ObservedAt ≤ 210s` (180s cadence + margin), **invalidated immediately** on a
   known failed refresh, invalid response, sleep/resume, or the window's reset boundary passing. A
   known failure means the app *knows* it cannot verify the reading — keeping it plain would be
   exactly the stale stand-in the rule bans. (Cross-settled: Sol's expiry, adopted.)
3. **Codex — SETTLED (c):** `now − ObservedAt ≤ CODEX_CURRENT_TTL`, never past `resets_at`, expiring
   to n/a. The TTL is **short but tuned to survive one long agentic turn** — not a rigid 5 minutes
   (which flickers to n/a mid-run at exactly the moment the display matters most). **Default: 20
   minutes, owner-tunable**, with the tuning rule stated in settings: longer than your longest typical
   turn, short enough that unseen usage (another device/session) stays bounded. Re-reading the same
   event does not renew it. Validate the default against real usage (E7).
4. Sanity: value within the pinned range, `resets_at` parseable and coherent.

### DATED — SETTLED (e): a separate "last known" area, truthful history, never current

- Rendered **only** in a visually distinct popup area: `Last known: 61% at Tue 14:02` — sourced from
  the `LastKnownReading` store, only while that reading's window has not reset.
- Never occupies a current-value row, **never drives threshold alerts**, never carries a live-looking
  countdown. The corresponding current field says n/a.
- **Monotone-floor exception (folded, with its caveat):** within one window, `used_percent` is
  monotone non-decreasing until reset under a constant plan/limit — so a dated near-limit reading is
  a hard *floor* on the current value. A dated reading at/above the warning threshold whose window has
  not reset **may legitimately drive the icon's WARNING SEVERITY, always paired with the unknown
  badge** — never a plain safe icon, and never a toast (toasts fire on LIVE only). Display it as
  "as of T", **not** "≥ X%" — a mid-window plan/limit change breaks the floor, so the qualified
  historical form is the only unconditionally true statement.

### N-A — everything else, always with a reason

Fetch failure, expiry, schema drift, auth rejection, reset passed with no newer reading, scale
unpinned, window not reported. Rendered as explicit "n/a" + a short reason ("throttled",
"authentication rejected", "source changed", "not reported", "no recent Codex event") + a remedial
hint where one exists. Grey/hatched visual treatment that cannot be misread as 0% used. Provider
cards never disappear — a visible n/a is part of the contract.

### Countdowns

Reset countdowns are deterministic derivatives of an authoritative `resets_at`, not forecasts.
Anchor Claude countdowns to the HTTP `Date` header where available and advance on a monotonic clock;
Codex uses synchronized Windows time. If time is known-unsynchronized or the reset timestamp is
inconsistent with a fresh observation: show the absolute reset time if valid, countdown n/a.

## 6. Security posture

Threat framing: the token necessarily goes to Anthropic to authenticate; the meaningful guarantee is
**it goes nowhere else**, and that guarantee is made externally checkable, not asserted.

- **Process split per §2** — only the short-lived probe ever touches `.credentials.json`; the token
  never enters argv, environment, stdout/IPC (probe output is usage JSON only), logs, config, crash
  dumps (WER exclusion), or the tray process. Managed strings can't be reliably scrubbed — the probe
  erases the token by *exiting*, which is the honest mechanism.
- **Externally enforceable one-host property:** an outbound firewall **deny** rule on
  `AIUsageTray.exe` (the probe is the only allowed caller, to one origin). This converts "the token
  goes to exactly one host" from a code-review claim into an OS-enforced, independently verifiable
  property. Recommended at first run; document the one-liner.
- **Never touch the refresh token.** Expired access token → n/a + "open Claude Code". The app holds
  only a short-lived credential it cannot renew — the least dangerous shape available.
- **Credential ACL check (folded correction — exact allowlist, warn-and-proceed):** before reading,
  the probe checks the file is a bounded-size regular file and audits its ACL. **OK: Owner, SYSTEM,
  Administrators** (the default Windows profile inheritance — flagging it would train alarm fatigue).
  **Warn on: Everyone, Users, Authenticated Users, or other named users.** Always **warn and
  proceed, never refuse** — the tool must not brick itself on cosmetic ACL variance, and it never
  modifies Claude's file or permissions.
- **Network:** direct connection is the secure default; proxy support only as an explicit opt-in
  setting carrying a plain warning that TLS-inspecting proxies can observe the credential. Redirects
  off, exact origin, default cert validation, no pinning (per §4.2).
- **No telemetry, no auto-update, no crash upload, no analytics.** Logging is allowlisted status/
  reason codes and the schema signature — never headers, bodies, tokens, or raw exceptions carrying
  them. Debug logging is opt-in and still never captures the Authorization header or bodies.
- **Supply chain, right-sized for a single-owner private tool (folded correction):** KEEP dependency
  pinning (`global.json`, locked SDK), zero third-party runtime packages on token paths, allowlisted
  logging. **OPTIONAL / DEFERRED until the tool is ever shared:** SBOM, reproducible builds,
  code-signing, package-channel distribution. The owner is one person building from source; a
  code-signing cert and a distribution pipeline are overhead with no threat-model payoff today.
- Least privilege elsewhere: runs as the user, no elevation, no services/drivers; reads exactly two
  dot-directories plus its own config dir; writes only its own config/log dir.

## 7. Display

### Icon semantics (settled regardless of the §9 form question)

- Severity from the **highest LIVE utilization** among applicable windows: normal < 80%, warning
  ≥ 80%, critical ≥ 90% (disclosed UI policy, owner-configurable; not predictions).
- **Never colour-only:** severity also changes shape/border/badge (high-contrast and colour-vision
  safe).
- **Unknown-state propagation (folded — non-negotiable):** never an unqualified green/safe icon while
  an *expected* provider or window is unknown. Known near-limit + something unknown → warning colour
  **plus** an unknown badge. Everything unknown → a neutral distinct all-unknown state ("?"), which
  reads as "cannot tell", never as "safe". The dated monotone-floor (§5) feeds warning severity +
  badge. n/a regions render as grey hatching that cannot be misread as an empty/safe gauge.
- Theme-aware (light/dark taskbar via `SystemUsesLightTheme`); re-add the icon on Explorer restart
  (`TaskbarCreated` — explicit kill-Explorer test case).

### Tooltip

Compact, explicitly labelled `% used`, worst/current figures per provider, `n/a` always explicit
(never omitted in a way that implies zero or safety):

```
Claude: 5h 42% · 7d 61% used
Codex:  7d 73% used · cr 12.4
```

Enforce truncation defensively before assigning `NotifyIcon.Text` (budget ~127 chars, verify on the
shipping runtime — E8); an unexpected limit must never throw from the render path. Reset times and
per-model detail are popup material, not tooltip material.

### Popup (WPF window anchored to the notification area)

- Two provider cards, fully independent, never disappearing on failure.
- Per card: every window the source currently reports (Claude 5h / Weekly / Opus wk / Sonnet wk when
  present, each n/a when absent; Codex whatever exists), each as label + bar + unrounded-honest %
  + absolute local reset time + live countdown where valid (§5 rules). Bars visualize; the adjacent
  number is authoritative.
- Codex credits + plan; Claude extra-usage/credits only when the response supplies value *and*
  meaningful unit.
- Per-metric observation time and freshness state; concise unavailable reasons; the **Last known**
  DATED area, visually separate (§5).
- Footer: per-provider last-refresh age, "Refresh now" (coalesced through the 180s gate — if gated,
  it visibly re-shows current state with its age), pause per provider, settings, quit.
- Right-click menu: Refresh now · Pause Claude polling · Start with Windows · Settings · Exit.
- **Build tasks named now (folded):** WPF has no tray-flyout primitive — positioning near the
  notification area across mixed-DPI monitors, PerMonitorV2 manifest, topmost, Deactivated-to-dismiss
  are all explicit work items. Both cards render on **one WPF dispatcher: async all the way down**, no
  sync waits anywhere on the UI thread — otherwise a block freezes both cards while the snapshots
  stay healthy, and provider isolation is proven for state but false for pixels (isolation tests must
  include UI-thread health).

### Notifications

- **Transition toasts:** Ok→Unavailable sustained > 5 min → one toast naming provider + reason.
  Flap-suppressed (no repeat for the same reason within 30 min). **Damped:** transitions *into*
  unknown do not toast on first flicker — the backoff ladder recovering a transient 5xx in 3–6 min
  is normal, not news.
- **Threshold toasts** (default on): one per window per crossing of the configured threshold,
  generated **only from LIVE observations**, reset when the window resets.
- **Mechanism is a build task, not an assumption (folded):** modern toasts from an unpackaged Win32
  app require identity/shortcut/COM activation registration, or the implementation silently becomes
  legacy balloons or nothing. Choose and test explicitly (E6); `NotifyIcon` balloon is the acceptable
  documented degrade.

### Windows integration (each a concrete build task — folded)

1. **Win11 tray-overflow demotion:** new icons land hidden in the overflow flyout and cannot be
   promoted programmatically. The glanceable premise fails until the user pins the icon → explicit
   first-run instruction (with a screenshot) is part of v1.
2. **Single-instance:** named mutex, second launch activates the existing instance (two instances =
   doubled polling against a grey endpoint + two icons).
3. **Auto-start:** HKCU `Run` value, toggleable — chosen and **tested in the actual deployment mode**
   (startup processes get a minimal environment; this is precisely why UA version resolution is
   cached, §4.2).

## 8. Form challenge — SETTLED

The tray is the right primary form: the requirement is cross-provider, persistent, and glanceable
with no terminal open. A statusline widget fragments across terminals and is invisible outside active
sessions; threshold-only notifications cannot answer "where am I now?". Both rivals are absorbed, not
rejected: threshold toasts ship (§7), and the sanitized snapshot model is consumable by a future
`--json` one-shot / statusline segment under the same accuracy contract — consumers, never second
collectors.

## 9. The ONE residual disagreement — tray-icon form, decided by a render test

**Not yet settled.** Both cross-reviews agreed on everything about the icon except its form:

- **One combined worst-of icon** (Fable): at 16 px an n/a bar is indistinguishable from an empty bar,
  and empty reads as *safe* — the implied-zero failure baked into pixels; two bars can't carry
  per-bar unknown badges at that size and lean on position memory.
- **Two fixed-position per-provider bars** (Sol): one combined signal deterministically hides *which*
  provider is constrained and can mask that the other provider is unavailable.

**Deciding build step (agreed by both sides): an actual 16 px notification-area render test.** Render
both candidates as real tray icons (not enlarged mockups) on the target machine: 100/125/150/200%
DPI, light and dark taskbars, high-contrast themes, non-standard taskbar colouring. Pass criteria per
candidate: (1) n/a is distinguishable from low usage at a glance; (2) the unknown badge is legible;
(3) warning vs critical is distinguishable without colour; (4) for the two-bar form, per-bar states
survive all of the above. Score both; the winner ships; the loser is recorded as the **documented
deliberate fallback** — and if the two-bar form fails, do not pretend a one-bar maximum still
represents both providers: the combined icon's semantics (worst-of + unknown badge) are the honest
single-signal contract, with per-provider detail one hover away.

**Synthesizer's lean (non-binding):** the combined worst-of icon with the unknown badge — the
"empty-bar-reads-safe" failure violates the owner's HARD RULE in pixels, while "which provider" costs
one hover. The render test decides.

## 10. Residual open questions — settle at build time

**Empirical prerequisites and tests (the build plan schedules these explicitly):**

- **E1 — Captured-payload spike (PREREQUISITE, gates all Claude rendering):** capture sanitized real
  `oauth/usage` responses; pin field spellings, utilization scale and precision, optional-credit
  units, `resets_at` format, `Retry-After` presence/format, null/absent variants. Commit as versioned
  fixtures; hard-code pinned semantics + range checks (§5).
- **E2 — Version discovery across install types:** npm shim (`claude.cmd`), native installer under
  `%LOCALAPPDATA%`, package managers, custom paths — including from a PATH-less startup environment.
  Validates the cache-last-known-good design (§4.2).
- **E3 — Codex file format / timestamp guarantee:** verify against real session files that in-support
  Codex versions embed per-line event timestamps; validate candidate-set bounds (file count, tail
  size) under real usage — long sessions, IDE-extension session files, bursts. Confirms or activates
  the pre-defined mtime-upper-bound fallback (§4.1).
- **E4 — 16 px icon render test:** the §9 decider.
- **E5 — Working set measured on the target machine** (release build; informational target ~40 MB
  framework-dependent — measured, not assumed, and not architecture-gating).
- **E6 — Toast mechanism:** identity/shortcut/COM registration for an unpackaged Win32 app vs the
  documented balloon degrade — choose and test (§7).
- **E7 — `CODEX_CURRENT_TTL` tuning:** validate the 20-min default against the owner's real longest
  agentic turns; revisit if the owner ever runs Codex on a second machine (unseen cross-device usage
  argues for a shorter TTL / more aggressive demotion to DATED).
- **E8 — `NotifyIcon.Text` budget on the shipping runtime** (127 vs legacy 63); truncation is
  enforced defensively either way.

**Judgement calls to confirm with the owner:**

- **O1 — projectvelox licence** at the exact commit, before any code/asset reuse (reading for
  behaviour needs no licence).
- **O2 — Credential rotation cadence in practice:** whether Claude Code rotates
  `.credentials.json` often enough during normal use that the no-refresh-token stance keeps Claude
  LIVE most working hours. If not, Claude reads n/a for stretches — correct under the contract, but
  the owner should ratify that availability trade-off rather than discover it.
- **O3 — Warning/critical thresholds** (defaults 80/90) and whether the firewall outbound-deny rule
  on the tray exe is applied at first run (§6 — recommended).
- **O4 — Enterprise-proxy support:** ship direct-only (recommended) or include the explicit opt-in
  proxy setting from day one.
