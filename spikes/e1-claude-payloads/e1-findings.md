# E1 — Claude `oauth/usage` payload capture & pinned semantics (SPIKE, TASK-005 / PLAN T3)

**Captured 2026-07-19** on the owner's machine via ONE compliant request:
`GET https://api.anthropic.com/api/oauth/usage`, `Authorization: Bearer <accessToken from
~/.claude/.credentials.json>`, `User-Agent: claude-code/2.1.191`. **HTTP 200.** Raw response saved
(verified free of tokens/UUIDs/PII) as `raw-capture-1.json` — the canonical fixture.

**This spike GATES all Claude-side rendering (M3).** The DTO and parser are hard-coded from the facts
below; runtime ambiguity → `Unavailable("scale-unpinned")`, never a heuristic guess.

## PINNED semantics (hard-code these)

- **Utilization scale is 0–100.** `five_hour.utilization = 28.0`, `seven_day.utilization = 37.0`
  (float, one implied decimal). The 0.42-fraction-vs-percent ambiguity is **RESOLVED: it's a percent
  0–100.** `limits[].percent` is the same number as an **integer** (28, 37, 43).
  → Render from the float `utilization` for precision; `limits[].percent` is the int rounding.
- **`resets_at` is ISO-8601 with microseconds + timezone offset**, e.g.
  `2026-07-19T19:59:59.592703+00:00` (UTC here). NOT epoch. Parse as `DateTimeOffset`.
- **Money is minor-units:** `spend.used = { amount_minor, currency:"USD", exponent:2 }` → dollars =
  amount_minor / 10^exponent. `extra_usage` carries the credits block (here `is_enabled:false`).

## The REAL shape — and how it CORRECTS the design (DESIGN.md §4.2)

The design/HANDOVER assumed top-level `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet`.
Reality (see `raw-capture-1.json`):

1. **`five_hour` / `seven_day` DO exist** — objects with `utilization` + `resets_at` (+ null
   `*_dollars` fields for dollar-metered plans).
2. **`seven_day_opus` / `seven_day_sonnet` exist as keys but are `null`** for this account, PLUS a
   crowd of other **null codename buckets** (`seven_day_oauth_apps`, `seven_day_cowork`,
   `seven_day_omelette`, `tangelo`, `iguana_necktie`, `omelette_promotional`, `nimbus_quill`,
   `cinder_cove`, `amber_ladder`, …). → **The "tolerate unknown/extra fields, never reject the
   envelope on them" rule is VALIDATED as essential** — a strict "unknown field = drift" parser would
   fail on first contact. Do NOT rely on the per-model *top-level* keys.
3. **`limits[]` is the richer, canonical array to render from.** Each entry:
   `{ kind, group, percent (int), severity, resets_at, scope, is_active }`.
   - `kind` ∈ {`session`, `weekly_all`, `weekly_scoped`}; `group` ∈ {`session`, `weekly`}.
   - **Per-model weekly comes from `weekly_scoped`** with `scope.model.display_name` (here `"Fable"`)
     and `is_active:true` — NOT from `seven_day_opus/sonnet`. This is the real source for the design's
     "per-model where available" display.
   - **The server already provides `severity`** (`normal` | … presumably `warning`/`critical`). The
     app may adopt the server severity OR keep its own 80/90 thresholds (DESIGN §7) — recommend
     computing our own from `percent` for the HARD-RULE guarantee, but the server `severity` is a
     useful cross-check / an additional field to tolerate.
4. **`spend`** block: `used.amount_minor/currency/exponent`, `percent`, `severity`, `enabled`,
   `disclaimer` (contains a markdown link — display-sanitize if ever shown). **`member_dashboard_available`** present.

## Recommended DTO / parse strategy (revises DESIGN §4.2)

- **Primary render source = `limits[]`** (carries kind/group/percent/severity/scope/is_active). Map:
  `session`→5h, `weekly_all`→weekly, `weekly_scoped`(is_active)→per-model weekly (label from
  `scope.model.display_name`). Cross-check `session`/`weekly_all` percents against
  `five_hour`/`seven_day` `utilization` for float precision.
- **Ignore all `null` buckets;** never treat their presence/absence as drift.
- **Drift detection** fires only when a *required* field the parser depends on
  (`limits[].percent`/`resets_at`, or `five_hour.utilization`) disappears, changes type, or leaves
  0–100 — then that metric → n/a; an untrustable envelope (non-JSON / HTML / missing `limits` AND
  `five_hour`) → all-Claude n/a(`source-changed`).

## Acceptance (TASK-005) — MET

- ✅ Versioned sanitized fixture committed (`raw-capture-1.json`, HTTP 200, no secrets).
- ✅ Pinned semantics written (scale 0–100 float; ISO-8601 `resets_at`; minor-unit money).
- ✅ Real shape documented + design DTO correction recorded.
- Follow-ups: capture a SECOND fixture at a materially different usage level (ideally near a known UI
  %) to reconfirm scale-by-correspondence, and one when `extra_usage.is_enabled` / a dollar-metered
  window is active, to pin those branches. (Non-blocking — the 0–100 scale is already unambiguous.)
