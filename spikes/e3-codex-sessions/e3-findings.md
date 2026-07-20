# SPIKE E3 — Codex `sessions/**/*.jsonl` rate-limit event shape

**Status:** read-only investigation, complete. Evidence = 72 real `.jsonl` files on
this machine (`~/.codex/sessions/`, spanning 2025-10-18 → 2026-07-19).
**Bottom line:** the assumed schema is close but wrong in the details that matter,
and there is real cross-version drift the collector must handle. Per-line timestamps
are **CONFIRMED** — the mtime-upper-bound fallback is **not** required (but see §2 for
the one caveat).

---

## TL;DR — pinned field paths the collector should hard-code

The rate-limit snapshot is **not** a top-level `type: "token_count"` event. It is a
top-level `type: "event_msg"` whose `payload.type == "token_count"`. Match on **both**.

| Fact | Actual JSON path (per line) | Notes |
|---|---|---|
| event selector | `.type == "event_msg"` **AND** `.payload.type == "token_count"` | assumption said top-level `type:token_count` — WRONG |
| per-line timestamp | `.timestamp` | **UTC** ISO-8601, e.g. `"2026-07-19T10:09:25.376Z"`. CONFIRMED present on every line |
| used_percent | `.payload.rate_limits.primary.used_percent` | JSON **number/float** (`4.0`, `100.0`); never fractional observed but parse as float |
| window (minutes) | `.payload.rate_limits.primary.window_minutes` | **NOT exact** — `299`/`300` = 5h, `10079`/`10080` = weekly. Classify by proximity, not `==` |
| reset (absolute) | `.payload.rate_limits.primary.resets_at` | **unix epoch SECONDS** (10-digit), e.g. `1785016789`. Newer files |
| reset (relative) | `.payload.rate_limits.primary.resets_in_seconds` | **older files only** — seconds-from-now offset. Must handle BOTH (see §1a) |
| second window | `.payload.rate_limits.secondary` | same sub-shape as `primary`, **or `null`** (current files) |
| credits balance | `.payload.rate_limits.credits.balance` | **string** decimal `"0E-10"` / `null`; whole object may be `null` (older) |
| credits flags | `.payload.rate_limits.credits.has_credits` / `.unlimited` | booleans; object may be `null` |
| plan | `.payload.rate_limits.plan_type` | `"plus"` / `"team"` / `null`. Absent in oldest files |
| limit id | `.payload.rate_limits.limit_id` | `"codex"` / `"premium"`; absent in oldest files. Advisory only |

Everything under `rate_limits` except `primary`/`secondary` must be treated as
**optional / nullable** — the oldest schema omits `limit_id`, `credits`, `plan_type`,
`individual_limit`, `rate_limit_reached_type` entirely.

---

## 1. The event that carries `rate_limits` — CONFIRMED shape

Every turn ends with a `token_count` event. Real, current (2026-07-19) line, verbatim
except pretty-printed:

```json
{
  "timestamp": "2026-07-19T10:09:25.376Z",
  "type": "event_msg",
  "payload": {
    "type": "token_count",
    "info": {
      "total_token_usage": { "input_tokens": 205100, "cached_input_tokens": 168448,
        "output_tokens": 1526, "reasoning_output_tokens": 277, "total_tokens": 206626 },
      "last_token_usage": { "...": "..." },
      "model_context_window": 258400
    },
    "rate_limits": {
      "limit_id": "codex", "limit_name": null,
      "primary":   { "used_percent": 4.0, "window_minutes": 10080, "resets_at": 1785016789 },
      "secondary": null,
      "credits":   { "has_credits": false, "unlimited": false, "balance": "0E-10" },
      "individual_limit": null, "plan_type": "plus", "rate_limit_reached_type": null
    }
  }
}
```

The assumption "each turn ends with an event `type: token_count`" is directionally
right but the literal `type` is `event_msg`; `token_count` is `payload.type`. Also note
the token-usage numbers the assumption didn't mention live under `payload.info` and are
useful (e.g. `model_context_window`).

### 1a. Schema drift is REAL — the collector must be version-tolerant

Mapping the **last** `rate_limits` event in all 72 files reveals four eras:

| Era (by file date) | `primary` window | reset field | `secondary` | `plan_type` | `credits` |
|---|---|---|---|---|---|
| 2025-10 (oldest) | `299` (5h) | `resets_in_seconds` | `10079` (weekly) | *absent* | *absent* |
| 2025-11 → 2026-04 | `300` (5h) | `resets_at` | `10080` (weekly) | `null`→`team` | `null`→object |
| **2026-07-15 → now** | **`10080` (weekly)** | `resets_at` | **`null`** | `"plus"` | `{balance:"0E-10"}` |
| degenerate (any era) | `null` | — | `null` | present | present |

Two hard consequences:

1. **Do NOT assume `primary` is the 5-hour window.** In 2025 `primary`=5h and
   `secondary`=weekly. In current files `primary`=**weekly** and `secondary`=`null`.
   The collector must **read `window_minutes` to decide which window each object is**,
   not rely on primary/secondary position. Recommended classification:
   `window_minutes <= ~360` → 5h bucket; `window_minutes >= ~1440` → weekly bucket.
2. **Handle both reset encodings:** `resets_at` (absolute epoch seconds) OR
   `resets_in_seconds` (relative). Prefer `resets_at`; if absent, compute
   `resets_at = <event .timestamp epoch> + resets_in_seconds`.

---

## 2. Per-line embedded timestamp — CONFIRMED

**Yes.** Every JSONL line carries a top-level `.timestamp`, ISO-8601 **UTC** with `Z`
and millisecond precision (`"2026-07-19T10:09:25.376Z"`). Within a file, timestamps are
strictly monotonic (append-only), so a `tail` read yields the newest event *in that
file*.

**The mtime-upper-bound fallback is NOT required** — the embedded timestamp is present
and reliable, and the collector should select the newest `token_count` event **by
`.timestamp`**, exactly as the task demands. One caveat to encode:

- **mtime and the filename timestamp are LOCAL; the embedded `.timestamp` is UTC.**
  Measured: file mtime `2026-07-19 11:09:25` (local, BST/UTC+1) == embedded
  `2026-07-19T10:09:25.376Z` + 1h, matching to the second. So **never compare an mtime
  against an embedded UTC timestamp directly** — you'd be off by the machine's UTC
  offset. Compare embedded-vs-embedded. mtime is fine for *pre-selecting candidate
  files* (§4), just not as a value that gets mixed into the UTC timeline.

---

## 3. Windows on this machine: weekly-only now, both windows historically — CONFIRMED

- **Every file from 2026-07-15 onward** (the 20 newest sessions) shows **weekly only**:
  `primary.window_minutes == 10080`, `secondary == null`. The 5-hour cap is gone.
- **Files 2025-10 through 2026-07-10** show **both**: a 5h window (`299`/`300`) **and** a
  weekly window (`10079`/`10080`).
- The cutover is sharply visible between **2026-07-10** (last file with `300`+weekly)
  and **2026-07-15** (first weekly-only file) — consistent with OpenAI removing the 5h
  Codex cap ~mid-July 2026.

Implication: the tray must **not** hard-require a 5h number. Current accounts will only
surface a weekly bar; the 5h slot should render as "n/a"/absent when `secondary`
(or whichever object classifies as 5h) is `null`.

---

## 4. Collector bounds (10 newest files / 64 KB tails / 14-day cap) — VALIDATED, with guards

**Byte distance of the last valid `rate_limits` event from EOF** (measured):

| File | Size | Last-valid event from EOF |
|---|---|---|
| newest 2026-07-19 | 5.3 MB | 1,060 B |
| 2026-07-10 (9.6 MB) | 9.6 MB | 1,371 B |
| degenerate 2026-04-30 | 2.1 MB | **5,489 B** (null-primary tail sits in front of it) |
| 29 MB session (2025-11-07) | 29 MB | 560 B |
| 17 MB session (2025-11-10) | 17 MB | 2,358 B |

Codex writes a `token_count` at the **end of every turn**, so the session file almost
always ends a few hundred bytes to a few KB after the last one — **even a 29 MB file's
last event is 560 B from EOF**. A **64 KB tail is generously sufficient** for every real
file observed (worst case ~5.5 KB). No file would defeat it on the "event too far from
EOF" axis.

**Three real edge cases that WILL defeat a naive "tail the newest file, take the last
token_count" collector** — each found in the corpus:

1. **Degenerate trailing event (null primary).** The *last* `token_count` line can have
   `primary: null, secondary: null` (seen 2026-04-16, 2026-04-30; carries
   `limit_id:"premium"`). Earlier lines in the same file are valid. → The collector must
   select the newest event whose **`primary` is non-null** (i.e. has a `used_percent`),
   not merely the last `token_count`. A 64 KB tail still contains the prior valid event
   (5.5 KB back), so the tail size is fine — the *selection predicate* is what matters.

2. **Session file with zero `token_count` events.** `2026-07-12` is a real 8-line,
   71 KB session (started, one user message, `task_complete`, no model turn) with **no
   `rate_limits` anywhere**. If this is the newest-by-mtime file the collector must
   **fall through to the next file**, not return empty. This is exactly why scanning
   ~10 files (not 1) is correct.

3. **Actively-written / partial last line.** A live session is being appended to; the
   final line can be a half-written JSON object. And any 64 KB *tail* read of a large
   file begins **mid-line** (a leading fragment). → Parse **line-by-line with
   try/catch**, silently skipping any line that fails to parse. Do **not** slurp the
   whole tail as one JSON stream (a single bad line aborts the whole read — verified:
   `jq` over the partial fixture dies on the leading fragment).

**On the 14-day cap:** benign and recommended (bounds the scan; stale usage shouldn't
display). One behavioural note — if Codex hasn't been used in >14 days, the cap excludes
*all* files and the provider returns "no data". That's the right default, but make it an
intentional, documented state rather than an empty crash.

**On "10 newest by mtime":** correct as a *candidate pre-filter*. mtime updates on every
append, and within-file timestamps are monotonic, so the newest-mtime file's last event
is normally the global newest — but because of edge cases 1 & 2, gather candidates from
the ~10 newest files and pick the max **embedded `.timestamp`** with a non-null
`primary`. (10 is comfortable headroom; on 2026-07-16 alone there were ~20 sessions, but
you only need enough to skip a run of empty/degenerate tails — 10 is fine.)

---

## Recommended collector algorithm (distilled)

1. Enumerate `~/.codex/sessions/**/*.jsonl`; drop files with mtime older than 14 days;
   take the ~10 newest by mtime.
2. For each, read the last ~64 KB; split on newlines; parse each line with try/catch,
   **skipping** unparseable lines (leading fragment + trailing partial).
3. Keep lines where `.type=="event_msg"` and `.payload.type=="token_count"` and
   `.payload.rate_limits.primary != null`.
4. Across all candidates, pick the one with the **max `.timestamp`** (UTC ISO-8601).
5. From `.payload.rate_limits`, classify `primary`/`secondary` into 5h vs weekly by
   `window_minutes` proximity (≤~360 → 5h, ≥~1440 → weekly). Read `used_percent`, and
   `resets_at` (fallback `event_ts_epoch + resets_in_seconds`) for each present window.
6. Read `plan_type`, `credits.balance` (string; may be `"0E-10"`/`null`), tolerating
   nulls/absence. Render a missing window (e.g. 5h on current accounts) as absent.

---

## Fixtures produced (all sanitized — only `timestamp` + `token_count`/`rate_limits`
fields; token-usage counts retained as they are non-identifying; every prompt, assistant
message, `cwd`, path, and id stripped)

| File | Purpose |
|---|---|
| `fixture-weekly-only.jsonl` | Current schema (2026-07): `primary` weekly, `secondary` null, `plan_type:"plus"`, `credits.balance:"0E-10"`, `resets_at`. 2 lines. |
| `fixture-legacy-both-windows.jsonl` | Line 1 = oldest era (`window_minutes` 299/10079, **`resets_in_seconds`**, no plan/credits). Line 2 = 2025-12 era (300/10080, `resets_at`, `credits` object, `plan_type:null`). Exercises both reset encodings + dual-window. |
| `fixture-degenerate-tail.jsonl` | Valid event then trailing **`primary:null`** event — tests "pick newest event with non-null primary", not last line. |
| `fixture-partial-line.jsonl` | Leading fragment (mid-line 64 KB start) + valid line + truncated final line (no trailing newline). Tests per-line try/catch tolerance. |

All fixtures verified: valid lines parse to the expected `used_percent`/`window`/`reset`;
fragments are skipped by a per-line reader (valid middle line recovered).
