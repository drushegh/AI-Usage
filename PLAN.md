# AI-Usage Tray — Build Plan (Fable, planning turn 1)

> **ROUTING CORRECTION (2026-07-19, post-plan):** this plan was authored assuming a *reach-in* build
> into `F:\Git\Personal\AI-Usage`. That was reverted — reach-in's `/add-dir` persistent-access layer
> is unreliable. **The build happens IN THE HUB under `projects/ai-usage/`** (DEC-030 workshop model);
> all code, fixtures and decision notes land there and commit to the hub. The finished build is
> **copied/extracted into `F:\Git\Personal\AI-Usage` and committed+pushed THERE at ship time**. The
> tasks below are unchanged — only substitute `projects/ai-usage/` for `F:\Git\Personal\AI-Usage` as
> the working location throughout.

Decomposition of `projects/ai-usage/DESIGN.md` (the settled crossbench spec) into per-feature board
tasks. One task = one independently-verifiable feature; every task is its own Verify story. Working
location is `projects/ai-usage/` in the hub (see routing correction above); the finished build extracts
to `F:\Git\Personal\AI-Usage` at ship. Sanitized E1/E3 fixtures are committed (scrubbed of tokens, org
UUIDs, and anything identifying) alongside the code.

Two ordering forces govern the sequence (per the brief and DESIGN §10):

1. **E1 gates all Claude work.** No Claude number is parsed, rendered, or even DTO'd on guessed
   semantics — the captured-payload spike pins scale/fields/formats first. E1 has zero code
   dependencies, so it starts day 1 *in parallel* with the Codex stream; it simply must be closed
   before any M3 task ships.
2. **Codex-only is the MVP shell.** The local/free/reliable half proves the entire chassis — domain
   model, snapshot store, provider isolation, TTL honesty, icon, tooltip, popup — before the fragile,
   ToS-grey Claude endpoint is touched.

---

## 1. Milestone plan

| # | Milestone | Goal | Exit criterion |
|---|---|---|---|
| **M1** | **Codex-only MVP shell** | Prove the whole chassis on the reliable half: tri-state model, store + isolated loops, Codex collector, TTL freshness, icon (form decided by E4), tooltip, popup with Codex card. No Claude code. | Tray runs all day on the target machine showing LIVE-or-honest-n/a Codex weekly % + credits/plan; icon severity + unknown states correct; E3 and E4 spikes closed. |
| **M2** | **Accuracy-contract hardening** | The full §5 LIVE / DATED / N-A machine: last-known history area, monotone-floor severity, countdown engine, sleep/reset invalidation, single-instance. | Fault-injection matrix (expiry, reset-passed, sleep/resume, missing windows) renders every state correctly; DATED never drives toasts or current rows; single instance enforced. |
| **M3** | **Claude provider behind E1** | The probe split, version cache, 180s gate + ladder + 429/401 policies, strict schema/drift validation, Claude card, opt-in/kill switch, logging discipline — token demonstrably confined. | Claude renders LIVE from the real endpoint under pinned E1 semantics or degrades to reasoned n/a; automated greps prove no token in any output/log/dump; all failure policies pass against a stub server. |
| **M4** | **Notifications, Windows integration, polish** | Toasts (E6-decided), autostart tested in real logon, Explorer-restart resilience, first-run pinning + firewall guidance, settings UI, install script, measurements (E5/E7). | All E-spikes closed; app survives Explorer kill, reboot and autostart from minimal env; working set measured; owner gates O2–O4 recorded. |

---

## 2. Blockers vs parallel streams

**Genuine prerequisite blockers (nothing downstream starts without them):**

- **T3 (E1)** → gates every M3 task that touches Claude semantics (T24, T26, T30, T31). Hard rule.
- **T2 (E3)** → gates T7 (the Codex collector's timestamp strategy is decided by it).
- **T9 (E4)** → gates T10's final icon form (the §9 residual; render test decides, loser is the
  documented fallback).
- **T4 → T5** (domain model → store/loop harness) → gate every collector and every UI task.

**Parallelizable streams (run alongside, no cross-dependency):**

- *Spike stream:* T2 (E3), T3 (E1), T9 (E4), T13 (E8), T22 (E2), T35 (E6) — research/harness work
  with no dependence on app code (T9/T13/T35 need only the T1 skeleton or a throwaway harness).
  All can start early; T3 and T22 in particular should start day 1.
- *UI shell stream:* T14 (popup shell), T16 (footer/menu) — parallel to the collector stream.
- *M3 tray-side policy tasks:* T27–T29 are testable against a fake probe in parallel with probe
  hardening (T24–T25).

**Live-endpoint discipline:** the Claude endpoint is undocumented and ToS-grey. E1 captures and all
live verification run at compliant cadence (≥180s, correct UA) — automated tests use a local stub
server; the real endpoint is touched manually and sparingly.

---

## 3. Ordered task list

Priorities: P0 = on the critical path of its milestone; P1 = required for v1; P2 = polish/measurement.
Tasks marked **SPIKE** are empirical spikes — acceptance is "the unknown is resolved + fixtures/
decision committed", not shipped UI.

### M1 — Codex-only MVP shell

**T1 — Solution scaffold + empty tray app boots** · P0 · §2
- Story: launching `AIUsageTray.exe` shows a placeholder tray icon and exits cleanly, proving the
  repo/build skeleton before any feature lands.
- Acceptance: solution in `F:\Git\Personal\AI-Usage` with `global.json` (pinned .NET 10 SDK) and
  `Directory.Build.props` (Nullable + TreatWarningsAsErrors on); `AIUsageTray` project with
  `UseWPF` + `UseWindowsForms`; test project runs green (framework + test platform recorded);
  placeholder `NotifyIcon` appears, Exit menu quits cleanly; zero third-party packages
  (CommunityToolkit.Mvvm permitted, UI layer only); clean build, zero warnings.
- Deps: none.

**T2 — SPIKE E3: Codex session-file format + timestamp guarantee** · P0 · §4.1, §10-E3
- Story: confirm against real `~/.codex/sessions` files that in-support Codex versions embed
  per-line event timestamps, and validate the candidate-set bounds, so the collector is built on
  verified facts.
- Acceptance: sanitized fixtures committed (weekly-only recent session, legacy both-windows session,
  long-session tail, a mid-write/partial-line sample); recorded decision: embedded timestamps
  confirmed OR the mtime-upper-bound fallback (§4.1) is activated; bounds (~10 newest files, ~64 KB
  tails, ~14-day cap) validated against real usage including IDE-extension session files and bursts.
- Deps: none (parallel with T1).

**T3 — SPIKE E1: captured Claude payloads → pinned semantics** · P0 · §5 (prerequisite), §10-E1 — **GATES ALL CLAUDE WORK (M3)**
- Story: capture sanitized real `oauth/usage` responses and pin field spellings, utilization scale
  and precision, optional-credit units, `resets_at` format, `Retry-After` presence/format, and
  null/absent variants — so no Claude number is ever rendered on guessed semantics (0.42 is legal
  under both scale readings; range sanity-checks cannot decide it).
- Acceptance: versioned sanitized fixtures committed to the target repo (multiple captures at
  different usage levels — ideally one near a known usage % visible in the Claude UI to pin the
  scale by correspondence); a written pinned-semantics note the parser will hard-code; the runtime
  rule restated: ambiguity → `Unavailable("scale-unpinned")`, never heuristic multiplication.
  Captures made *outside the app* by script, compliant UA, compliant cadence, token never committed.
- Deps: none — start day 1 in parallel with the Codex stream.

**T4 — Tri-state domain model (the accuracy contract in types)** · P0 · §3
- Story: `Metric<T>` / `MetricState` / `UsageWindow` / `ProviderSnapshot` / `LastKnownReading`
  exactly per §3, so an estimated, defaulted, or carried-forward value is structurally
  unrepresentable.
- Acceptance: types match §3 (window identity by `WindowMinutes`, `decimal` unrounded, per-metric
  `ObservedAt`/`ReasonCode`); unit tests prove: no construction path yields `Available` without
  Value + ObservedAt; missing window → `NotApplicable("not-reported")`, never zero; sibling metrics
  degrade independently; reset-passed means `Unavailable`, never zeroed. `TimeProvider` injected
  wherever time enters (all freshness logic testable with a fake clock).
- Deps: T1.

**T5 — Snapshot store + isolated provider-loop harness** · P0 · §4
- Story: provider loops publish immutable snapshots into an atomic store; the UI renders only from
  the store; one provider's failure, hang, or parse error cannot touch the other.
- Acceptance: with fake providers — a throwing provider yields an `Unavailable` snapshot with
  reason, sibling unaffected; a hanging provider is bounded by timeout; store swap atomic; change
  events marshalled to the WPF dispatcher; startup shows n/a until first observation; a
  resume-invalidation hook exists (fully wired in T20); adding a third provider = one adapter + one
  registration line (no plugin loading).
- Deps: T4.

**T6 — Config + rate-control persistence (never usage data)** · P1 · §2 (Persistence)
- Story: thresholds/toggles, rate-control metadata (last attempt / next eligible), and the
  last-known-good Claude Code version survive restart; usage responses never touch disk.
- Acceptance: config round-trips an app restart; writes confined to the app's own config dir; a test
  asserts no snapshot/usage payload is ever serialized to disk; no secrets in config.
- Deps: T1.

**T7 — Codex collector: latest `rate_limits` from session JSONL** · P0 · §4.1
- Story: the tray gets real Codex weekly (and 5h where present) %, credits and plan from the newest
  valid `token_count` event — zero network, zero auth.
- Acceptance: driven by T2 fixtures + a live-machine check — greatest VALID embedded event timestamp
  across the whole candidate set wins (file mtime only narrows candidates, never determines truth);
  `CODEX_HOME` honoured; absent dir → `Unavailable("no-sessions-dir")`; `FileSystemWatcher`
  (recursive, `*.jsonl`, ~2s debounce) plus 60s reconciliation poll; partial UTF-8 tail line
  discarded; sharing violation / mid-write → retry next cycle, provider stays up; per-file parse
  error skips the file, not the provider; windows mapped by `window_minutes` (300/10080/generic
  label); missing 5h window → `NotApplicable("not-reported")`; zero network I/O on this path.
- Deps: T2, T5.

**T8 — Codex LIVE freshness: `CODEX_CURRENT_TTL` engine** · P0 · §5 (LIVE rule 3)
- Story: a Codex figure renders as current only within the TTL (default 20 min, owner-tunable) and
  never past its `resets_at` — expiry demotes to honest n/a.
- Acceptance: fake-clock tests: within TTL → LIVE; past TTL or past `resets_at` → not LIVE
  (n/a, reason "no-recent-event"); re-reading the same event does NOT renew the TTL; TTL read from
  config with the tuning rule captured for the settings text (T41).
- Deps: T7, T6.

**T9 — SPIKE E4: 16 px icon render test → icon-form decision (the §9 residual)** · P1 · §9, §10-E4
- Story: settle combined-worst-of vs two-per-provider-bars by rendering both as REAL notification-
  area icons on the target machine — evidence, not opinion.
- Acceptance: throwaway harness renders both candidates as actual tray icons at 100/125/150/200%
  DPI, light + dark taskbars, high-contrast themes, non-standard taskbar colouring; scored against
  the four §9 pass criteria (n/a distinguishable from low usage at a glance; unknown badge legible;
  warning vs critical without colour; two-bar per-bar states surviving all of the above); winner
  recorded + screenshots committed + the loser documented as the deliberate fallback; if two-bar
  fails, the combined worst-of + unknown badge is the honest single-signal contract.
- Deps: T1 (harness only). Must close before T10 finalizes.

**T10 — Tray icon: worst-of LIVE severity in the winning form** · P0 · §7 (Icon semantics)
- Story: the glanceable icon — severity from the highest LIVE utilization (normal <80 / warning ≥80 /
  critical ≥90, owner-configurable), never colour-only, theme-aware.
- Acceptance: winning E4 form implemented; thresholds compared on unrounded values; severity also
  changes shape/border/badge (verified via greyscale + high-contrast screenshots); light/dark
  taskbar via `SystemUsesLightTheme` with regeneration on theme change.
- Deps: T7, T8 (data), T9 (form).

**T11 — Icon unknown-state propagation (badge + all-unknown "?")** · P0 · §7 (Unknown-state propagation)
- Story: the icon can never read "safe" while an expected provider or window is unknown.
- Acceptance: state-matrix tests: known near-limit + anything expected unknown → warning colour PLUS
  unknown badge, never plain safe; everything unknown → the neutral "?" state, distinct from both
  safe and warning; n/a regions grey-hatched, never readable as an empty/safe gauge.
- Deps: T10.

**T12 — Tooltip: compact "% used" with explicit n/a** · P1 · §7 (Tooltip)
- Story: hover gives per-provider worst/current figures labelled "% used"; n/a is always explicit —
  never omitted in a way that implies zero or safety.
- Acceptance: matches the §7 format (`Claude: 5h 42% · 7d 61% used` / `Codex: 7d 73% used · cr 12.4`);
  n/a rendered literally; defensive truncation before every `NotifyIcon.Text` assignment (budget
  from T13; the render path can never throw on length); reset times and per-model detail
  deliberately excluded (popup material).
- Deps: T7, T13.

**T13 — SPIKE E8: `NotifyIcon.Text` budget on the shipping runtime** · P2 · §7, §10-E8
- Story: verify the 127-vs-63-char budget empirically so truncation is fact-based.
- Acceptance: measured on the actual .NET 10 runtime on the target machine; number recorded and the
  truncation constant set from it; the defensive guard stays regardless of the answer.
- Deps: T1 (micro-spike — do alongside T12).

**T14 — Popup shell: WPF flyout anchored to the notification area** · P0 · §7 (Popup), §2
- Story: clicking the icon opens a correctly-positioned, topmost, click-away-dismissed popup on any
  monitor/DPI mix — WPF has no tray-flyout primitive, so this is explicit work.
- Acceptance: PerMonitorV2 manifest; correct positioning near the notification area at
  100/125/150/200% and on multi-monitor mixed-DPI (crisp, not blurry, on the secondary monitor);
  topmost; dismiss on `Deactivated`; async all the way down — no sync waits on the dispatcher
  (review gate), and the provider-isolation tests extended to UI-thread health (a hung fake provider
  must not freeze the popup); keyboard-reachable with UIA names (Narrator announces the cards).
- Deps: T1. Parallel with the collector stream.

**T15 — Codex provider card** · P0 · §7 (Popup), §5
- Story: the popup's Codex card shows every window the source currently reports — label + bar +
  unrounded-honest % + absolute local reset time (+ countdown once T19 lands) — plus credits + plan,
  per-metric observation time/freshness, and concise n/a reasons; the card never disappears.
- Acceptance: fixture-driven matrix: weekly-only, legacy both-windows, all-n/a, mid-write; bars
  visualize while the adjacent number is authoritative; one-decimal fixed display rule with
  unrounded threshold comparisons; card renders reasoned n/a states; card present even when the
  provider is `Unavailable`.
- Deps: T7, T8, T14 (countdown cell may read n/a until T19).

**T16 — Popup footer + tray right-click menu** · P1 · §7
- Story: footer (per-provider last-refresh age, Refresh now, pause per provider, settings, quit) and
  the menu (Refresh now · Pause Claude polling · Start with Windows · Settings · Exit).
- Acceptance: Codex Refresh-now triggers an immediate rescan; Claude-specific entries exist but are
  inert/greyed ("not configured") until M3 wires them (T27/T32); Start-with-Windows inert until T38;
  footer ages tick live; quit exits cleanly.
- Deps: T14.

### M2 — Accuracy-contract hardening

**T17 — `LastKnownReading` store + DATED "Last known" popup area** · P1 · §3, §5 (DATED)
- Story: truthful history — when a current field goes n/a, a visually-distinct area shows
  `Last known: 61% at Tue 14:02`, only while that window has not reset.
- Acceptance: the store survives snapshot replacement; renders ONLY in the distinct area; suppressed
  once the window's reset passes; never occupies a current-value row, never carries a live-looking
  countdown; the corresponding current field simultaneously shows n/a; DATED can never drive
  threshold toasts (asserted now, re-asserted in T37).
- Deps: T8, T15.

**T18 — Monotone-floor: dated near-limit drives warning severity + badge** · P1 · §5 (monotone-floor), §7
- Story: within an unreset window, `used_percent` is monotone non-decreasing — so a dated at/above-
  threshold reading keeps the icon at warning + unknown badge, never plain safe, and never toasts.
- Acceptance: state tests: dated ≥ warning threshold & window unreset → warning + badge; window
  reset passes → normal unknown handling resumes; displayed as qualified history ("as of T"), NOT
  "≥ X%" (a mid-window plan change breaks the floor); no toast fires from this path.
- Deps: T17, T11.

**T19 — Countdown engine (deterministic, monotonic, refuses to guess)** · P1 · §5 (Countdowns)
- Story: reset countdowns are derivatives of an authoritative `resets_at`, advanced on a monotonic
  clock — not forecasts.
- Acceptance: Codex countdowns anchored to synchronized Windows time; an anchor extension point for
  the Claude HTTP `Date` header (consumed by T31); known-unsynchronized time or a reset timestamp
  inconsistent with a fresh observation → absolute reset time if valid + countdown n/a; fake-clock
  tests including skew.
- Deps: T15.

**T20 — Resume-from-sleep + reset-boundary invalidation** · P1 · §4, §5, §3
- Story: waking the machine, or a window's reset boundary passing, immediately invalidates expired
  observations BEFORE any refresh starts — no stale flash, and reset-passed never means "zero".
- Acceptance: power-resume event → expired observations invalidated first, refreshes second; reset
  boundary passing → that window's metrics `Unavailable` until a fresh authoritative observation;
  fake-clock tests plus one manual sleep/resume verification on the target machine.
- Deps: T8 (extended to Claude by T30).

**T21 — Single-instance guard** · P1 · §7 (Windows integration 2)
- Story: a second launch activates the existing instance — never two icons or doubled polling
  against a grey endpoint.
- Acceptance: named mutex; second launch surfaces the existing instance (shows the popup) and exits;
  rapid double-launch race tested. Must be in place before Claude polling goes live (M3).
- Deps: T1.

### M3 — Claude provider behind E1 (every task below is gated on T3)

**T22 — SPIKE E2: Claude Code version discovery across install types** · P1 · §4.2 (UA/version), §10-E2
- Story: verify version resolution against npm shim (`claude.cmd`), native installer under
  `%LOCALAPPDATA%`, package managers and custom paths — including a PATH-less startup environment —
  validating the cache-last-known-good design.
- Acceptance: findings per install type committed as a short decision note (+ any probe scripts);
  resolution order fixed (validated install metadata → time-limited `claude --version`); the
  PATH-less startup scenario demonstrably falls back to the cache.
- Deps: none (parallelizable from M1).

**T23 — Version resolution + last-known-good cache in the tray** · P0 · §4.2
- Story: the tray resolves the installed Claude Code version once, caches it persistently,
  re-resolves opportunistically, and never fabricates one.
- Acceptance: resolution per T22; cache persisted via T6; re-resolve on startup, on version-file
  change, and daily; `n/a("no-version")` only if no version has EVER been resolved on this machine;
  version passed to the probe via argv; minimal-environment startup test passes using the cache.
- Deps: T22, T6.

**T24 — `ClaudeUsageProbe.exe`: credential read + one request + validated emit** · P0 · §2(b), §4.2 (HTTP posture), §6
- Story: a short-lived BCL-only process reads `.credentials.json`, makes the one request with the
  argv-supplied version UA, emits usage JSON over redirected stdout, and exits — the token is erased
  by process exit, the honest mechanism.
- Acceptance: BCL-only (`HttpClient`, `System.Text.Json`; zero package refs verified);
  `AllowAutoRedirect = false` and ANY 3xx → protocol failure; TLS ≥ 1.2, default cert validation
  (never relaxed), no pinning; 15s timeout; hard-coded origin `https://api.anthropic.com` + path
  `/api/oauth/usage`; UA exactly `claude-code/<argv-version>`, never rotated/spoofed; token appears
  in no argv/env/stdout/stderr/log under success AND failure runs (automated grep test over all
  captured outputs); the refresh token is never read or used; WER/LocalDumps exclusion configured
  and `DbgEnableMiniDump` off — verified by forcing a probe crash and confirming no dump persists;
  distinct exit codes for auth-rejected / throttled / transport / schema-invalid; green against the
  E1 fixtures via a local stub server. Launched by absolute path, no shell, serialized (installed-
  layout verification completes in T42).
- Deps: T3 (E1), T23.

**T25 — Credential ACL audit (warn-and-proceed)** · P1 · §6
- Story: before reading, the probe checks bounded-size regular file + audits the ACL against the
  exact allowlist — and warns without ever refusing or modifying anything.
- Acceptance: Owner/SYSTEM/Administrators (default profile inheritance) → silent; Everyone / Users /
  Authenticated Users / other named users → one warning surfaced in the tray UI, read proceeds;
  never modifies Claude's file or permissions; tests with crafted ACLs on a fixture file.
- Deps: T24.

**T26 — Tray-side schema validation + drift detection (probe output is untrusted)** · P0 · §4.2 (Schema validation), §5
- Story: strict DTOs parse `five_hour` / `seven_day` / `seven_day_opus` / `seven_day_sonnet`
  independently under the E1-pinned semantics; drift degrades honestly, never to a guess.
- Acceptance: pinned semantics + range checks hard-coded from T3; runtime scale ambiguity →
  `Unavailable("scale-unpinned")`; unknown ADDITIONAL fields tolerated with a non-sensitive
  name/type schema signature recorded on change; a known field disappearing / changing type /
  violating its range → that metric n/a; untrustable envelope → ALL Claude metrics
  n/a(`"source-changed"`) + one-time notification + backoff to cap; response bodies never logged
  (test-asserted); a malformed-fixture suite drives every path.
- Deps: T3, T5 (developable against fixtures in parallel with T24).

**T27 — 180s hard gate + trigger coalescing + backoff ladder** · P0 · §4.2 (cadence)
- Story: one request per 180 seconds no matter what; transient failures walk 3/6/12/24/48-capped-60
  minutes.
- Acceptance: fake-clock tests: timer, manual refresh, app restart, network restoration, resume, and
  credential-file change all coalesce through the one gate, none bypasses; probe launches serialized
  (max one in flight); failures walk the exact ladder; success resets it; Refresh-now while gated
  visibly re-shows current state with its age (wires T16's entry live).
- Deps: T5, T24 (testable against a fake probe first).

**T28 — 429 handling + UA-rejection diagnostic state** · P1 · §4.2 (429)
- Story: honour a valid `Retry-After` when larger than the ladder step; after 5 consecutive 429s at
  compliant cadence, stop presenting "throttled" and surface `Unavailable("endpoint-or-UA-changed")`
  — an endless "throttled" loop is a lie.
- Acceptance: stub-server tests: Retry-After larger / smaller / absent / invalid; 5×429 flips to the
  diagnostic state with a one-time notification, continuing at the 60-min cap; a later success
  recovers cleanly; UA is never rotated in response.
- Deps: T27.

**T29 — 401/403 stop + credential re-arm** · P1 · §4.2 (401/403)
- Story: auth rejection stops polling with the hint "open Claude Code to refresh sign-in"; a
  credential change re-arms polling automatically.
- Acceptance: single 401 → one immediate credentials re-read + one retry within the same logical
  attempt (the one documented coalescing exception); still rejected → `Unavailable("auth-rejected")`
  + hint, polling stops; PARENT-DIRECTORY watcher detects create/change/rename (atomic replacement
  and file-absent-at-startup both covered) with a periodic stat fallback; all re-arm retries flow
  through the 180s gate.
- Deps: T27.

**T30 — Claude LIVE expiry (210s + immediate invalidation)** · P0 · §5 (LIVE rule 2)
- Story: a Claude figure is current only ≤ 210s from observation, and is invalidated the instant the
  app KNOWS it cannot verify the reading.
- Acceptance: fake-clock tests for the 210s bound and each invalidation trigger — known failed
  refresh, invalid response, sleep/resume, reset boundary passing; invalidated → n/a with the DATED
  area (T17) taking over where eligible.
- Deps: T26, T20.

**T31 — Claude provider card** · P0 · §7 (Popup)
- Story: the popup's Claude card — 5h / Weekly / Opus-wk / Sonnet-wk each shown when present and n/a
  when absent; extra-usage/credits only when the response supplies value AND meaningful unit;
  observation times, freshness, reasons; never disappears.
- Acceptance: fixture matrix incl. absent windows, credit variants, all-n/a, drift states; countdown
  anchored to the HTTP `Date` header via T19's extension point; unrounded-honest % + one-decimal
  display rule; card present under every failure mode.
- Deps: T26, T30, T14, T19.

**T32 — Claude opt-in + kill switch** · P0 · §4.2 (Opt-in)
- Story: the remote provider is explicit opt-in (undocumented, ToS-grey) and pausable at any time;
  off → Claude fields read honest n/a; no hidden fallbacks exist.
- Acceptance: default OFF on fresh install; enabling starts the loop, pausing/disabling stops it
  safely mid-backoff; disabled renders the card with n/a (reason "disabled"/"paused"), never a
  missing card; code-review assertion that no cookie, browser-automation, or local-estimate path
  exists anywhere (the local token/cost files are never converted into a limit-%); T16's "Pause
  Claude polling" entry now live.
- Deps: T27, T16.

**T33 — Allowlisted logging discipline** · P1 · §6
- Story: logs can only ever contain allowlisted status/reason codes and the schema signature — never
  headers, bodies, tokens, or raw exceptions carrying them; opt-in debug logging is equally clean.
- Acceptance: fault-injection runs (auth failure, schema drift, 429 storm, probe crash) followed by
  an automated log grep: no Authorization material, no body fragments, no token substrings; raw
  exceptions from token paths wrapped/sanitized; debug mode re-tested identically.
- Deps: T24, T26.

**T34 — First-run firewall recommendation + documented one-liner** · P2 · §6, §10-O3(b)
- Story: convert "the token goes to exactly one host" from a code-review claim into an OS-enforced,
  independently-verifiable property — an outbound deny rule on `AIUsageTray.exe`.
- Acceptance: a first-run recommendation (choice recorded, never nags again) + the exact documented
  one-liner; manually verified: with the rule applied the tray has zero egress while the probe still
  reaches `api.anthropic.com`; owner's O3(b) decision recorded.
- Deps: T24, T42 (rule targets the installed path).

### M4 — Notifications, Windows integration, polish

**T35 — SPIKE E6: toast mechanism for an unpackaged Win32 app** · P1 · §7 (Notifications), §10-E6
- Story: modern toasts from unpackaged Win32 need identity/shortcut/COM-activation registration —
  choose and TEST the mechanism, or knowingly take the documented `NotifyIcon` balloon degrade;
  never let it silently become nothing.
- Acceptance: both candidates exercised from the real install layout on the target machine; the
  decision + registration steps (or the degrade) recorded; the chosen mechanism demonstrably fires.
- Deps: T1 (T42's layout strengthens the test).

**T36 — Transition toasts (damped, flap-suppressed)** · P1 · §7 (Notifications)
- Story: Ok→Unavailable sustained > 5 min → one toast naming provider + reason; first flicker never
  toasts; no same-reason repeat within 30 min.
- Acceptance: fake-clock tests: a 4-min blip (ladder recovering a transient) → silent; sustained →
  exactly one toast; flap-suppression window enforced; reason changes re-arm correctly.
- Deps: T35, T5.

**T37 — Threshold toasts (LIVE-only)** · P1 · §7 (Notifications)
- Story: one toast per window per crossing of the configured threshold, generated ONLY from LIVE
  observations, re-armed when the window resets.
- Acceptance: crossing from LIVE fires exactly once per window per crossing; DATED / monotone-floor
  states never toast (re-asserting T17/T18); re-arm on window reset; thresholds shared with icon
  severity config, compared unrounded.
- Deps: T35, T8, T30.

**T38 — Auto-start toggle (HKCU Run), verified in deployment mode** · P1 · §7 (Windows integration 3)
- Story: "Start with Windows" works from a real logon's minimal environment — exactly the scenario
  the version cache exists for.
- Acceptance: HKCU `Run` value pointing at the installed absolute path; toggleable from menu and
  settings; verified by an actual sign-out/sign-in: app starts, Codex provider comes up, Claude
  version resolves from cache with no PATH; toggle-off removes the value.
- Deps: T42, T23, T16.

**T39 — Explorer-restart resilience (`TaskbarCreated`)** · P1 · §7 (Icon semantics)
- Story: killing or restarting Explorer re-adds the icon automatically.
- Acceptance: the explicit kill-Explorer test: icon returns without an app restart, popup still
  functional; stable across repeated restarts.
- Deps: T10.

**T40 — First-run overflow-pinning instruction** · P2 · §7 (Windows integration 1)
- Story: Win11 lands new icons hidden in the overflow flyout and they cannot be promoted
  programmatically — v1 tells the user to pin it, with a screenshot, or the glanceable premise
  silently fails.
- Acceptance: shown once at first run (state recorded via T6), screenshot-illustrated,
  re-accessible from settings.
- Deps: T10, T6.

**T41 — Settings UI** · P1 · §7, §5, §6, §10-O3/O4
- Story: every owner-tunable in one place — warning/critical thresholds (defaults 80/90 — O3),
  `CODEX_CURRENT_TTL` with its tuning rule stated inline, Claude opt-in/pause, autostart, toast
  toggles, and (only if the owner opts to ship it — O4) the explicit proxy setting with its
  TLS-inspection warning.
- Acceptance: every §-named tunable present, persisted via T6, applied without restart where
  reasonable; the TTL setting displays the tuning rule ("longer than your longest typical turn,
  short enough that unseen usage stays bounded"); thresholds effective immediately on unrounded
  comparisons; O3 and O4 owner decisions recorded in the target repo.
- Deps: T6, T16, T32.

**T42 — Install layout + build script (per-user, `%LOCALAPPDATA%\Programs`)** · P1 · §2 (Deployment)
- Story: a repeatable script produces the per-user unpackaged install — the layout that probe
  launching, autostart, the firewall rule and the toast test all point at.
- Acceptance: installs/updates to `%LOCALAPPDATA%\Programs\` with no elevation; framework-dependent
  build runs on the target machine (self-contained fallback documented); probe launched by absolute
  install-dir path works from the installed layout; no MSIX.
- Deps: T1. (Pull earlier if T24/T34/T35/T38 want the real layout sooner.)

**T43 — SPIKE E5: working-set measurement** · P2 · §2, §10-E5
- Story: measure idle working set and CPU of the release build on the target machine against the
  informational targets (~40 MB framework-dependent, ~0% idle CPU).
- Acceptance: measured numbers recorded (release build, ≥30 min idle, both providers running);
  informational — not architecture-gating; egregious surprises filed as follow-up tasks.
- Deps: M1 complete (final numbers after M3).

**T44 — SPIKE E7: `CODEX_CURRENT_TTL` validation + O2 ratification** · P2 · §5, §10-E7, §10-O2
- Story: validate the 20-min TTL default against the owner's real longest agentic turns, and let the
  owner ratify the Claude availability trade-off after observing real credential-rotation behaviour.
- Acceptance: roughly a week of real usage observed; longest-turn data vs the TTL recorded, default
  kept or retuned; the cross-device caveat noted (a second Codex machine argues for a shorter TTL);
  O2: the owner explicitly ratifies — or revisits — the no-refresh-token stance after seeing how
  much of the working day Claude actually stays LIVE.
- Deps: T8 (live usage), T32 (for the O2 half).

---

## 4. Owner gates (not board tasks — decisions the plan consumes)

- **O1 — projectvelox licence:** only relevant if any code/asset/fixture reuse is ever proposed; the
  settled from-scratch build means the default is "never copy". Check the exact licence at the exact
  commit before any exception. (Reading source for endpoint behaviour needs no licence.)
- **O2 — credential-rotation reality:** folded into T44 — ratified after real observation, not
  guessed up front.
- **O3 — thresholds (80/90) + firewall at first run:** consumed by T41 and T34.
- **O4 — proxy support:** decide before T41 ships. Recommended: direct-only for v1; the opt-in proxy
  setting is additive later.

---

## 5. Start here

**Pick up T1 (scaffold), T2 (E3 spike) and T3 (E1 capture) together** — the two spikes are
independent research that de-risks both halves of the build while T1 puts the skeleton up; T3 in
particular must be underway from day 1 because all of M3 waits on it.
