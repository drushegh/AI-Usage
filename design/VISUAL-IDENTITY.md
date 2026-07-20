# AI-Usage — Visual Identity (consolidated build spec)

Synthesised (via `/crossbench fable,sol`) from two independent designs and their mutual cross-reviews. Positions the
cross-reviews converged on are marked **Settled** and are not re-argued here. Only §7 is
open — everything else is buildable as written. Scope: Windows 11 tray app, .NET 9 —
WPF popup + settings, WinForms `NotifyIcon` tray, **zero third-party runtime
dependencies**. The LIVE/DATED/n-a accuracy contract is sacred throughout. No
vendor-trademark evocation: amber and teal appear only together, as halves of our own
instrument.

---

## 1. Design principles — the organising invariant

> **Saturation carries trust; hue carries meaning; channel separates meanings.**
> Full-chroma fill = LIVE. Desaturated + hatched = DATED. No fill = n-a.
> Provider hues (amber/teal) answer only "whose data?". Severity hues (green/yellow/red)
> answer only "act now?". Interaction blue answers only "clickable/focused?".
> No two meanings ever share a visual channel in the same frame.

Derived rules, all **Settled**:

1. **Three semantic channels, never four.** Provider identity, current severity,
   interaction. Accuracy (LIVE/DATED/n-a) is *not* a hue family — it is carried by
   saturation + shape (hatch, dashes, emptiness) + words + timestamps. The blue/purple
   accuracy palette from design B is deleted; grey survives only as ink, not as a
   semantic hue.
2. **LIVE is unmarked.** One affirmative freshness line per popup (header, "Updated
   18s ago") asserts currency for every unmarked figure. Only the exceptions — DATED
   and n-a — carry chips. Bright `ink/primary` numerals appear **only** on LIVE values;
   this is the single most enforceable rule against a stale number reading current.
3. **Quiet when healthy.** No OK capsule, no green in the popup at rest. The resting
   grammar is silence so that *any* severity ink is signal. The tray tooltip carries the
   affirmative "all clear" wording — the one surface with no other reassurance channel.
4. **Severity never recolours data.** Bars, rings and cards keep provider colour;
   severity appears only as glyph + word + coloured numeral adjacent to the figure it
   qualifies, as threshold ticks, and as the tray arc. Severity is always accompanied by
   a non-colour cue (shape + label).
5. **Register: dashboard utility.** Chrome recedes; colour is reserved for meaning.
   Exactly **one signature**: the instrument language — the twin-stop gauge motif and
   its threshold ticks, recurring identically in app icon, tray glyph, popup bars, and
   README graphics. No side rails (both designs' rails are dropped — the border-left
   stripe is a known generative tell, and severity was triple-encoded), no icon tiles,
   no letter-spaced uppercase eyebrows, no nested cards (window → card, nothing deeper),
   border OR shadow per surface never both, no pure black, all neutrals tinted.
6. **Stable identity, volatile instrument.** The brand mark never changes; the tray
   glyph's whole job is to change. They share one geometry, different ink (§2.4).
7. **Dark-only at v1, plus a real Windows High Contrast dictionary.** No half-baked
   light theme. Everything token-named so light becomes a dictionary swap later, not a
   redesign. **Settled.**

---

## 2. Icon system

### 2.1 Primary silhouette — the Twin-Stop Gauge

Charcoal rounded-square tile containing two equal, opposing meter arcs with open gaps
centred at 12 and 6 o'clock — amber left (Claude), teal right (Codex). Flat arc
terminals make each gap read as a hard limit stop, not a decorative broken ring. Empty
centre; no letters, no percentage, no third colour, no vendor-like geometry. Two equal
complete arcs = two first-class meters in one application; the static icon never
pretends to show actual usage.

Master geometry (256×256, verified internally consistent):

- Tile: `(16,16)`–`(240,240)`, corner radius `48`. Fill `#182027`, keyline `#34414D`.
- Gauge centre `(128,128)`; arc centreline radius `70`; stroke `28`; flat caps.
- Right arc `−76°…76°` (152°); left arc `104°…256°` (152°); two 28° gaps centred at
  12 and 6 o'clock. Transparent outer canvas.
- No notches, no ticks, no gradients, no texture (subtraction pass applied — the
  optional terminal notches from design B are cut).

At 16 px the required read is: **dark tile + amber left bracket + teal right bracket**.
No other taskbar icon is half-amber-half-teal; the two-hue split is the recognition
feature.

### 2.2 Fallback ladder (gated on the 16 px render test — see §6.6 and §7-Q1)

The cross-reviews genuinely disagreed here, so the spec commits a primary and a
fallback rather than re-arguing:

1. **Primary:** twin brackets as specced above (2 px gaps at 16 px).
2. **First remediation if the 16 px lineup test shows the gaps smearing shut:** widen
   the small-size gaps (16 px: 2 px → 3 px; 24 px already widens), re-test.
3. **Committed fallback if brackets still fail:** one continuous 270° horseshoe —
   bottom opening 90° centred at 6 o'clock, clean amber/teal **colour junction at
   12 o'clock** (no transparent top gap at small sizes; a subtle hairline divider at
   ≥48 px only). Geometry: centre `(128,134)` (nudged 6 px below true centre for
   optical balance), arc centreline r `69`, stroke `30`, round caps ≥48 px, square
   below.

Whichever silhouette passes ships **everywhere** — brand mark, tray instrument, README
— one geometry, no mixing.

### 2.3 Degradation ladder 256 → 16 (per-size hand-tuned masters, never naive downscales)

| Size | Tile (inset / radius) | Arc r / stroke | Gaps | Keyline | Notes |
|---|---|---|---|---|---|
| 256 | 16 / 48 | 70 / 28 | 28° | 2 px `#34414D` | flat caps |
| 64 | 4 / 12 | 17.5 / 7 | 28° | 1.5 px | flat caps |
| 48 | 3 / 9 | 13 / 5 | 30° | 1 px | drop all secondary detail |
| 32 | 2 / 6 | 9 / 3.5 | 32° | 1 px | pixel-fit to grid |
| 24 | 1 / 5 | 6.5 / 3 | ~40° (2 px) | 1 px | gaps widened to survive AA |
| 16 | 1 / 3 (14×14 tile) | ~4.5 / 2 | 2 px | omit if it muddies | fully hand-snapped |

All small sizes (32/24/16) are separately authored pixel-fit SVG masters; 48/64 render
from the 256 master. The icon must never depend on fine ticks or miniature text.

### 2.4 Brand mark vs tray instrument — two glyphs, one geometry (**Settled**)

- **Brand mark (static .ico):** the gauge at rest, fully inked amber/teal on the tile.
  Used by: exe resource, Start Menu, taskbar, Alt-Tab, WPF window chrome, installer,
  Add/Remove Programs. **Never changes, never communicates status, never appears in
  the notification area.**
- **Tray instrument (`IconRenderer`, live):** same silhouette, drawn as an instrument
  (§2.5). **Never used for identity surfaces; installer branding never uses it.**
- Before the first successful fetch the tray shows the unknown instrument (track +
  `?` badge) — never the brand mark and **never a fake green**. **Settled.**

### 2.5 Tray instrument geometry

Drawn at **physical pixels** for the taskbar's monitor: `S = 16 × dpi/96` (20 @125%,
24 @150%, 32 @200%); all measures are ratios of S. Layers, primary variant:

- **Tile field:** the dark tile (`tray/tile #182027`, corner radius 0.19×S) with a
  1-physical-px solid keyline `tray/keyline #6B7680` — the tile guarantees contrast on
  light taskbars; the keyline keeps the edge on dark ones. (Committed fallback if the
  16 px test shows the tile crowding the arcs: drop the tile, arcs on transparent with
  a 1 px halo — then the theme-variant tokens in §3 apply. See §7-Q3.)
- **Track:** the full silhouette path in opaque `tray/track #6B7680` (3.6:1 on the
  tile). **Never a transparent alpha grey** — `#80808080` composites to ~1.8:1 on a
  white taskbar and is banned. **Settled.**
- **Fill arc:** severity-coloured, swept along the track path from the lower-left
  terminus clockwise, length proportional to the **worst LIVE metric's** percentage,
  quantised to whole device pixels. **No artificial minimum arc** — an 18° floor
  fabricates magnitude and is banned; zero usage honestly shows track only (the badge
  disambiguates "LIVE at 0%" from "no data"). **Settled.**
- **Centre glyph (S ≥ 20 only):** hand-pixeled warn triangle or critical `!` in the
  severity colour. At S = 16 the centre glyph is dropped; if the `?` badge is present
  it **replaces** the glyph rather than joining it (badge > glyph).
- **Badge:** `?` disc at the upper-right, diameter ~0.38×S (6 px at S=16), disc
  `tray/badge-disc #C9D2DA`, hand-pixeled glyph `tray/badge-ink #0E1216` (10.8:1).
  Present whenever coverage is incomplete (§4.5).
- **Theme/DPI lifecycle:** re-render on taskbar theme change, DPI/display change, and
  taskbar recreation — even when usage data has not changed (§6.4).
- **High Contrast:** when `SystemInformation.HighContrast` is true, render tile =
  `SystemColors.Window`, track/arcs/glyph/badge = `SystemColors.WindowText`; state is
  carried by shapes and the badge only.

---

## 3. Colour tokens (dark theme — the shipped identity)

Ground: **cool, near-neutral charcoal** (hue ≈ 207°, chroma deliberately low so the
ground neither merges with nor privileges teal; warm charcoal is rejected — it
harmonises Claude-amber into its own ground and worsens the amber/warning
discrimination). **Settled.** No zero-chroma grey, no `#000`.

### Surfaces

| Token | Hex | Job |
|---|---|---|
| `bg/window` | `#11171C` | popup + settings canvas |
| `bg/card` | `#182027` | provider cards, combo popups, chips' ground |
| `bg/hover` | `#202A33` | hover surface |
| `bg/pressed` | `#1D262E` | pressed surface |
| `bg/inset` | `#2B3640` | bar tracks, input wells |
| `bg/selection` | `#30425D` | list/combo selection (pre-composited 25% action over card) |
| `stroke/hairline` | `#34414D` | card border, separators (border OR shadow, never both) |
| `stroke/disabled` | `#2A343E` | disabled control borders |

### Ink

| Token | Hex | Job | Contrast (on `bg/card`) |
|---|---|---|---|
| `ink/primary` | `#F3F6F8` | **LIVE numerals only**, headings | ~15:1 |
| `ink/secondary` | `#B7C0C8` | labels, DATED numerals, **chip text** | 8.9:1 |
| `ink/muted` | `#88949F` | captions, freshness line, chip borders | 5.3:1 — **banned on `bg/inset`** (4.0:1, fails body text) |
| `ink/disabled` | `#5A646E` | disabled text | graphics-only contrast |

DATED chip text uses `ink/secondary`, never a dim "unknown grey" — a 3.3:1 chip label
was an accessibility failure in design A and is corrected here. **Settled.**

### Provider

| Token | Hex | Job |
|---|---|---|
| `provider/claude` | `#EAA93E` | Claude LIVE fills, name underline, README ring |
| `provider/codex` | `#43C9B8` | Codex LIVE fills, name underline, README ring |
| `provider/claude-dated` | `#A3834E` | DATED Claude fill — **exact pre-composited solid**, not an alpha (3.5:1 on `bg/inset`) |
| `provider/codex-dated` | `#549186` | DATED Codex fill — same discipline (3.4:1 on `bg/inset`) |

Never use raw alpha for DATED fills — 40% amber over charcoal reads as a murky third
identity colour and cannot be baselined. These two hexes are the only DATED fill
colours in the product. **Settled.**

### Severity

| Token | Hex | Where it may appear | Non-colour cue (mandatory) |
|---|---|---|---|
| `sev/ok` | `#5BCB7A` | **tray arc only** — the popup shows no colour when healthy | (tray) tooltip wording |
| `sev/warn` | `#FFD166` | tray arc; popup warn glyph + word + numeral + tick | outline triangle + "Warning" |
| `sev/crit` | `#FF6675` | tray arc; popup crit glyph + word + numeral + tick (5.8:1 on card — passes body text; no separate crit-text token needed) | filled triangle / `!` + "Critical" |

**Honesty note (Settled):** `sev/warn` sits ~5° of hue from `provider/claude` — they
are the *same hue family*, separated by lightness (+ saturation), shape, position and
label, **not** by hue. The popup therefore goes through the CVD/grayscale matrix
(§6.6); if warn/amber confusion appears, **lighten warning toward `#FFE08A` — never
shift it to orange** (orange collides worse and re-opens the channel fight).

### Accuracy (no hue family — **Settled**)

| Token | Hex | Job |
|---|---|---|
| `accuracy/hatch` | `#10161C` | solid 45° hatch lines over `*-dated` fills; 4 DIP pitch, 1.25 DIP stroke |

Everything else DATED/n-a uses ink tokens + shape: dashed chip borders in `ink/muted`,
chip text in `ink/secondary`, empty dashed track outline in `ink/muted`.

### Interaction

| Token | Hex | Job |
|---|---|---|
| `action/primary` | `#78A9FF` | buttons, links, selection, focus ring — **interaction only, never data state** (7.0:1 on card; hue 218° — verified distinct from Codex teal 172°) |
| `action/on-primary` | `#0E1216` | text/glyphs on filled primary buttons |
| `action/hover` | `#8FB8FF` / `action/pressed` `#6C9BF0` | filled-button states |
| destructive | reuse `sev/crit` | destructive buttons, validation borders/messages (settings only) |

Blue is **hard-coded, deliberately** — do **not** substitute the user's Windows accent
colour: an amber-ish or teal-ish accent would re-break both provider symmetry and
channel separation. **Settled.**

### Tray (physical-pixel instrument; all opaque)

| Token | Hex | Job |
|---|---|---|
| `tray/tile` | `#182027` | glyph field (primary variant) |
| `tray/keyline` | `#6B7680` | 1 px outer keyline |
| `tray/track` | `#6B7680` | on-tile track (3.6:1 vs tile) |
| `tray/badge-disc` / `tray/badge-ink` | `#C9D2DA` / `#0E1216` | `?` badge |
| `tray/track-dark` / `tray/track-light` | `#59636D` / `#767F88` | **no-tile fallback only**: opaque theme variants (3.2:1 on `#101010`; 4.1:1 on white) |
| `tray/ok-light` / `tray/warn-light` / `tray/crit-light` | `#1F7A38` / `#9C6A00` / `#C42B35` | **no-tile fallback only**: light-taskbar severity, darkened by lightness with hue preserved (4.7–5.6:1 on white) |

Theme detection: `HKCU\...\Themes\Personalize\SystemUsesLightTheme`, read per render.

### High Contrast (**Settled** — a floor, not a feature)

Ship `HighContrast.xaml` as a **system-colour resource dictionary**, swapped in when
`SystemParameters.HighContrast` is true (listen on
`SystemParameters.StaticPropertyChanged`):

| Role | SystemColors |
|---|---|
| surfaces | `Window` / `Control` |
| text | `WindowText` / `ControlText` |
| borders, tracks, ticks | `WindowText` |
| selection, focus | `Highlight` / `HighlightText` |
| disabled | `GrayText` |
| links/actions | `HotTrack` |

Provider identity in HC is carried by the **name text**; severity by glyph + word;
accuracy by hatch/dash/emptiness + words. Nothing in the product is colour-only, so HC
strips hue without stripping meaning. The tray HC policy is in §2.5.

---

## 4. Accuracy grammar (LIVE / DATED / n-a)

### 4.1 The three states — popup grammar

| State | Fill | Numeral | Chip | Extra channel |
|---|---|---|---|---|
| LIVE | provider colour, full chroma | `ink/primary`, bright | **none — LIVE is unmarked** | header freshness line is the affirmative claim |
| DATED | `*-dated` token + 45° `accuracy/hatch` overlay | `ink/secondary` — visibly not bright; "Last known 72%", never an isolated large number | dashed-outline chip (`ink/muted` border), clock glyph, text `ink/secondary`: `as of 14:32` | hatch = shape change, survives grayscale/CVD |
| n-a | none — empty track, dashed `ink/muted` outline | `—` em-dash; **never a fabricated 0%** | dashed chip `n/a`; "Not available" + optional concise reason | absence itself |

Do not use opacity alone for DATED — the words, timestamp, hatch, and demoted
hierarchy do the semantic work. **Settled.**

### 4.2 Per-metric freshness (**Settled** — the card-collapse correction)

Freshness is tracked **per metric** (each limit window's fetch), not per provider.

- A **card-level** DATED chip is permitted **only when every displayed figure in that
  card shares one snapshot** (same fetch state + timestamp). Otherwise chips render
  **per row**, and only on the non-LIVE rows.
- The headline worst-% **inherits the freshness of its source metric**: if the worst
  metric is DATED, the headline takes the full DATED grammar ("Last known 72%",
  `ink/secondary`, chip).
- Reset countdowns inherit their metric's freshness. A DATED row never shows a live
  countdown; it shows the snapshot's absolute reset time inside the DATED grammar
  ("reset 16:00 · as of 14:32") or nothing.
- The headline and tray tooltip must **name the driving metric** ("82% · 5h window") —
  an unlabelled worst-% can silently jump between windows and read as a discontinuity.

### 4.3 The affirmative freshness line

Popup header, `ink/muted`, 12 DIP: `Updated 18s ago` — where the age shown is the
**oldest** LIVE figure on display (conservative direction: never claim fresher than
true). Every unmarked figure is covered by this claim; every figure not covered
carries its own chip. When nothing is LIVE: `No live data` in `ink/secondary`.

### 4.4 Historic severity inside DATED grammar (**Settled** — the information-destruction fix)

Honesty about currency must not destroy actionable information. When a DATED metric's
last-known state was warn/crit, the popup renders it **strictly inside the DATED
grammar**: `was Critical · 14:32` — word + **outline** glyph in `ink/secondary`,
muted, on the hatched row. Never the live capsule treatment, never full-chroma
severity colour, never a bright numeral. The tray tooltip leads with the same wording
when the only recent data is DATED-critical.

### 4.5 Tray mixed-state truth table (**Settled** — normative, exhaustive)

Rules first:

- **R1** — Arc colour *and* length derive **only from LIVE metrics**: the worst LIVE
  metric drives both (one metric, one colour, one length — never mixed sources).
- **R2** — Any provider not fully LIVE ⇒ `?` badge. **Solid severity colour in the
  tray always means LIVE.**
- **R3** — No LIVE data ⇒ **no arc, ever**. No grey arcs, no dated magnitude, no
  desaturated severity. Track + badge only.
- **R4** — At S = 16 the badge replaces the centre glyph; at S ≥ 20 they may coexist.
- **R5** — DATED magnitude/severity appear only in the tooltip and popup, in DATED
  grammar (§4.4).
- **R6** — Pre-first-fetch = row 6. Never green by default.

| # | Provider states (either order) | Arc | Badge | Centre glyph (S≥20) | Tooltip |
|---|---|---|---|---|---|
| 1 | LIVE + LIVE | worst-LIVE severity colour, worst-LIVE % length | — | warn ▲ / crit `!` if applicable | `AI-Usage — all clear · Claude 42% (5h) · Codex 31% (5h)` (healthy) or the warn/crit line first |
| 2 | LIVE + DATED | from the LIVE provider only | `?` | per LIVE severity | LIVE line first unless dated-was-warn/crit: then `Codex — was Critical · 14:32` leads |
| 3 | LIVE + n-a | from the LIVE provider only | `?` | per LIVE severity | `Codex — no data` second line |
| 4 | DATED + DATED | **none** (track only) | `?` | — | both `was …` lines; historic warn/crit leads |
| 5 | DATED + n-a | **none** (track only) | `?` | — | `was …` + `no data` |
| 6 | n-a + n-a (incl. before first fetch) | **none** (track only) | `?` | — | `AI-Usage — no data yet` |

Tooltip stays within the `NotifyIcon.Text` limit (≤127 chars); each provider's state is
always spelled out.

### 4.6 Timestamps (**Settled**)

- Same-day: time only — `14:32` (user's locale/format).
- Older than today: date + time — `Jul 18, 14:32`.
- Full localized timestamp **with timezone/UTC offset** in the row tooltip and in
  `AutomationProperties.HelpText` (accessible text).
- Freshness line uses relative age (`18s ago`, `2m ago`); DATED always uses absolute.

---

## 5. Styling

### 5.1 Typography

`FontFamily="Segoe UI Variable Text, Segoe UI"` (Win10 falls back automatically). No
display face, no monospace crutch, no letter-spaced uppercase.

| Role | Size / weight |
|---|---|
| Headline worst-% | 22 semibold, tabular |
| Provider name (card title) | 14 semibold |
| Popup app name / settings section headers | 13 semibold / 14 semibold |
| Body, labels, values, severity words | 12 regular (values tabular) |
| Captions, chips, timestamps, freshness line | 12 regular — the 11 DIP floor from the drafts is raised: 11 DIP is below Windows' comfortable floor at 100% scale unless the render test proves otherwise |

All numerals: `Typography.NumeralStyle="Lining"` +
`Typography.NumeralAlignment="Tabular"` so 47%→48% moves zero layout pixels.
Right-align comparable percentages.

### 5.2 Rhythm and geometry

4-DIP base unit. Popup width **380 DIP** (fixed). Popup inset 12; card padding 16; card
gap 8; card radius 8; control radius 6; borders 1; bar height 8; bar row rhythm 28.
`UseLayoutRounding="True"` + `SnapsToDevicePixels="True"` app-wide; PerMonitorV2 (no
baked bitmaps in-app — everything is geometry). Win11 rounded window corners via DWM;
Win10 square-corner fallback accepted (never `AllowsTransparency` for a cosmetic
radius). Solid surfaces — no Mica/Acrylic (Win10 parity). **Settled.**

### 5.3 Popup structure and card anatomy

Top to bottom: header row (app name left; freshness line right, §4.3) → Claude card →
Codex card → compact actions row (Refresh, Settings — text buttons, `ink/secondary`,
hover `bg/hover`, focus ring `action/primary`).

Cards are stacked, **identical in geometry, fixed order, never collapsed** because a
provider lacks data — structural symmetry keeps both providers first-class.
**Settled.**

Card anatomy (no side rails — **Settled**):

- **Header row (~40 DIP):** left — provider name, 14 semibold `ink/primary`, with a
  2-DIP underline in the provider colour exactly the width of the name (the identity
  cue; an underline, not a border stripe). Right — headline worst-% (22 semibold,
  `ink/primary` iff LIVE) with its metric label beneath in 12 `ink/muted`
  ("5h window"), and, when warn/crit and LIVE, the severity glyph + word + coloured
  numeral adjacent (`sev/warn` outline ▲ "Warning" / `sev/crit` filled ▲ "Critical").
  Healthy → nothing (quiet).
- **Metric rows (28 DIP each):** single line — label 12 `ink/secondary` (fixed left
  column ~110 DIP), bar centre (flexible), value 12 tabular right + reset caption 12
  `ink/muted` ("resets 2h"). DATED rows swap the caption for the chip (§4.1).

### 5.4 Bars as instruments; rings' role

**Rings summarise; bars detail.** Popup uses **linear bars** (fast comparison, long
labels, many limits in a narrow surface). Rings remain in the abstract icon motif and
the large README overview graphics — two layouts of one meter system: dark track,
provider-coloured body, rounded geometry, same tokens. README figures with invented
values are labelled `SAMPLE`; product screenshots keep the real accuracy treatment.
README hero PNGs are re-rendered with these tokens in the same release that ships the
popup restyle — one commit, one identity. **Settled.**

Bar spec (the signature detail):

- Track: `bg/inset`, 8 DIP tall, fully rounded (radius 4).
- Fill: provider colour (or `*-dated` + hatch), radius 4.
- **Threshold ticks:** at the warn and crit percentages, a 1-**device-pixel** slit in
  `bg/card`, full bar height, drawn **after** (over) the fill so the fill can never
  bury them; snapped to device pixels (`RenderOptions.EdgeMode="Aliased"` on the tick
  layer). The limit is visible *before* you cross it. Provisional thresholds
  75% / 90% pending §7-Q4.
- Hatch (DATED): `DrawingBrush`, `TileMode="Tile"`, `ViewportUnits="Absolute"`,
  `Viewport="0,0,4,4"`, one 45° line, stroke 1.25 DIP `accuracy/hatch`. Verify at
  100/125/150/200% for moiré/solid-block collapse; if 125% moirés, snap the pitch to
  whole device pixels per DPI bucket.
- The **mini-ring** from design A is omitted at v1 (headline + bars carry the summary);
  see §7-Q2.

### 5.5 Settings window (full state coverage — **Settled** correction)

~420×480 DIP minimum, `bg/window`, native title bar with the brand icon, **DWM dark
title bar**: `DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE /*20*/, TRUE)`
(retry attribute 19 on pre-20H1 Win10). Sections: Providers · Refresh & notifications
· Appearance · Diagnostics · About. Label column ~140 DIP + control column; 14 semibold
`ink/primary` section headers; hairline separators; 24 DIP section gaps. Controls
≥32 DIP tall. Connection/readiness figures obey the same accuracy grammar as the popup.
Provider blocks have equal geometry (name + underline, no rails). Red (`sev/crit`) only
for destructive actions and validation.

Restyling `Background/BorderBrush/Foreground` alone is **not** a dark theme. Required
state coverage, all from tokens:

| Control | Rest | Hover | Pressed | Disabled | Focus / other |
|---|---|---|---|---|---|
| Button (secondary) | transparent, border `stroke/hairline`, text `ink/secondary` | `bg/hover`, text `ink/primary` | `bg/pressed` | text `ink/disabled`, border `stroke/disabled` | 2 DIP `action/primary` ring, 2 DIP offset |
| Button (primary) | `action/primary` / `action/on-primary` | `action/hover` | `action/pressed` | `stroke/disabled` fill, `ink/disabled` text | same ring |
| TextBox | `bg/inset`, border `stroke/hairline` | border `#4A5866` | — | `ink/disabled` | focus border `action/primary`; validation border + message `sev/crit` (12 DIP) |
| CheckBox / Radio | 16 DIP box, border `ink/muted` | border `ink/secondary` | — | `stroke/disabled` | checked fill `action/primary`, glyph `action/on-primary` |
| ComboBox | as TextBox + chevron `ink/secondary` | — | — | — | **popup**: `bg/card`, border `stroke/hairline`; item hover `bg/hover`; selection `bg/selection` + `ink/primary` |
| ScrollBar | 8 DIP, track transparent | thumb `#4A5460` | `#59636D` | — | thumb rest `#34414D`, radius 4 |

Tray context menu: **native** WinForms `ContextMenuStrip` default rendering — a
hand-themed tray menu is chrome fighting the OS. **Settled.**

### 5.6 Motion (**Settled**)

| What | Duration / easing |
|---|---|
| Bar fill width, tray-arc sweep, headline value | 180 ms decelerating ease-out (`CubicEase, EaseOut`) |
| State swaps — chip/glyph/severity appear-disappear | 150 ms opacity ease-out |
| Everything else | none — nothing else moves |

**No animation on first paint** (skip when the previous value is null). Honour reduced
motion: `SystemParameters.ClientAreaAnimation == false` → all durations zero (snap).
No bounce, no fades-everywhere.

### 5.7 WPF constructs (zero runtime deps)

- `Theme.xaml` — every §3 token as `SolidColorBrush` (`x:Key="Brush.Provider.Claude"`
  …); the app never hard-codes a hex. `HighContrast.xaml` swapped per §3.
- Bars: `ControlTemplate` on `ProgressBar` or a small `UsageBar : FrameworkElement`
  overriding `OnRender` (track, fill, hatch, ticks — in that order).
- Arcs/rings (README/tray renderer shares the math): `Path` + `ArcSegment`, or a
  ~30-line `Arc` helper emitting `StreamGeometry` (point-on-circle + `IsLargeArc`).
- Hatch: the `DrawingBrush` of §5.4 — no image assets.
- Token unity: one small `design-tokens.json` generates `Theme.xaml` and the marketing
  pipeline's `tokens.css` at build time — one source of truth, zero runtime cost.

---

## 6. Production

### 6.1 Sources and pipeline (determinism honesty — **Settled**)

```
assets/icon/src/mark-256.svg      master (also renders 64/48)
assets/icon/src/mark-32.svg       pixel-fit master
assets/icon/src/mark-24.svg       pixel-fit master
assets/icon/src/mark-16.svg       pixel-fit master
assets/icon/png/                  rendered PNGs — COMMITTED, authoritative
assets/icon/AiUsage.ico           assembled — COMMITTED
tools/icon-env.lock               pinned Chromium version + platform
tools/IcoBuilder/                 ~120-line net9 console tool, no packages
tools/build-icon.ps1              ONE command: render (pinned Chromium) + assemble + hashes
```

SVGs use only paths/fills/strokes — no text, filters, fonts, or linked resources.
Render via pinned headless Chromium (`--headless --screenshot --window-size=N,N
--default-background-color=00000000`, device scale factor 1, sRGB) at
16/24/32/48/64/256.

**Determinism, stated honestly:** Chromium output is stable *within one pinned build on
one platform*, not byte-identical across OSes/CPUs. Therefore: render in **one pinned
CI environment** (or the one blessed machine), **commit the PNGs as source of truth**,
review re-renders as visual diffs like any asset change. Hashes (SHA-256 of PNGs and
ICO, recorded by `build-icon.ps1`) catch **accidental drift only** — do **not** gate CI
on cross-machine byte-identity; a red check people learn to ignore is worse than none.
ICO **assembly** from committed PNGs *is* fully deterministic and may be CI-verified.

### 6.2 IcoBuilder and .ico layout

`tools/IcoBuilder` (net9, `<UseWPF>true</UseWPF>` for the in-box `PngBitmapDecoder`;
zero packages) writes the container by hand: ICONDIR (reserved 0, type 1, count) +
16-byte entries (width/height bytes, 0 = 256; planes 1; bitcount 32; size; offset).
Frame encoding — the conservative, universally handled layout: **32bpp BGRA BMP DIBs
(`BITMAPINFOHEADER`, height doubled, all-zero AND mask) for 16–64; raw PNG for 256
only.** (Chosen as the compatibility-safe layout; the "all-PNG misrenders on
still-supported builds" anecdote is not elevated to a product claim. **Settled.**)
Fixed frame order ⇒ byte-identical output for identical inputs.
`dotnet run --project tools/IcoBuilder -- assets/icon/png -o assets/icon/AiUsage.ico`.

### 6.3 WPF hookups

- csproj: `<ApplicationIcon>assets\icon\AiUsage.ico</ApplicationIcon>` (PE resource →
  Explorer, taskbar, Alt-Tab).
- Include the .ico as `Resource`; set `Icon="/assets/icon/AiUsage.ico"` explicitly on
  the popup host window (if applicable) and the settings window.
- App manifest: PerMonitorV2 `dpiAwareness`.

### 6.4 WinForms tray hookups — HICON ownership (**Settled** correction)

`NotifyIcon` **never** receives the static brand icon — `IconRenderer` output only.
The safe swap (clone-owning pattern; the naive `Icon.FromHandle` + late `DestroyIcon`
leaks under exceptions):

```csharp
IntPtr h = bmp.GetHicon();
Icon owned;
try { using var tmp = Icon.FromHandle(h); owned = (Icon)tmp.Clone(); }
finally { NativeMethods.DestroyIcon(h); }          // destroy the temp handle NOW
Icon? prev = _current;
_notifyIcon.Icon = owned;                           // assign the clone
_current = owned;
prev?.Dispose();                                    // dispose the previous clone
```

Defined ownership on every path: shutdown (`Visible = false`, `Icon = null`,
`_current?.Dispose()`, `_notifyIcon.Dispose()`), exception paths (the `finally`
above), and re-registration + re-render on:

- **TaskbarCreated** (`RegisterWindowMessage("TaskbarCreated")` — Explorer restart);
- **DPI / display change** (`WM_DPICHANGED`, display-settings change) — re-query the
  taskbar monitor's DPI (`Shell_TrayWnd` → `MonitorFromWindow` → `GetDpiForMonitor`)
  and re-render at the new S;
- **Theme change** (`WM_SETTINGCHANGE` / `ImmersiveColorSet`; `SystemUsesLightTheme`)
  — re-render even when usage data is unchanged.

### 6.5 MSI hookups

WiX (names are WiX-specific; `ARPPRODUCTICON` itself is MSI-generic — confirm once the
MSI toolchain is fixed): `<Icon Id="AiUsageIco" SourceFile="assets\icon\AiUsage.ico"/>`;
`<Property Id="ARPPRODUCTICON" Value="AiUsageIco"/>` (Add/Remove Programs);
`Icon="AiUsageIco"` on the Start-menu `Shortcut`. Installer and bootstrapper branding
**never** use the dynamic severity icon.

### 6.6 Verification matrix (never ship unviewed; measurable criteria — **Settled**)

- **Icon:** composite 16/20/24/32 px renders onto real light + dark Win11 taskbar
  screenshots at 100/125/150/200%; plus grayscale and deuteranopia/protanopia sims.
  **Pass protocol (Q1 gate):** fixed 10-icon lineup, ≥5 viewers, native 16 px: ≥80%
  locate the mark and report it as *two* coloured arc segments (brackets pass); if
  viewers report one fused ring, walk the §2.2 ladder.
- **Popup state matrix:** render {LIVE-ok, LIVE-warn, LIVE-crit, DATED, n-a} per
  provider, mixed cards, and per-row mixed freshness; view every frame. Grayscale
  pass: LIVE vs DATED vs n-a rows classified correctly ≥90% from the grayscale sheet.
  CVD pass: warn/crit frames under deuteranopia + protanopia sims — warning must not
  be confusable with Claude-amber fills (remedy per §3: lighten toward `#FFE08A`,
  never orange).
- **Contrast:** scripted WCAG check of every committed pair in §3 (4.5:1 body, 3:1
  large text/graphics), including the banned pair (`ink/muted` on `bg/inset` must
  fail — that is why it is banned).
- **Settings:** capture every control × state cell of §5.5, incl. ComboBox popup,
  scrollbar, validation, focus, and the dark title bar.
- **Tray:** render every truth-table row of §4.5 at S = 16/20/24/32 on both taskbar
  themes; verify badge legibility (Q3) and that no frame shows severity colour without
  LIVE data.
- Commit all rendered sheets as review baselines so future changes diff loudly.

---

## 7. Residual questions — settle in the build-time render-and-look loop

1. **Icon silhouette gap-count.** Primary = twin brackets (gaps at 12 & 6); committed
   remediation ladder = widen 16 px gaps → continuous 270° horseshoe with a colour
   junction at 12 (§2.2). Gate: the §6.6 lineup protocol at native 16 px.
2. **Does the mini-ring earn its place?** v1 default = no ring (headline + bars). Add
   only if a render test shows viewers can identify both its metric and its state and
   it beats headline+bars on comprehension.
3. **16 px `?` badge / centre-glyph legibility.** Committed fallbacks, in order:
   badge-replaces-glyph at S = 16 (already normative, §2.5/R4); badge-shape-only
   coding (disc without glyph); drop-the-tile variant (arcs on transparent + 1 px
   halo, switching to the theme-variant tray tokens).
4. **Final warn/crit threshold percentages** (product decision). The 75% / 90% in
   §5.4 are provisional; the ticks and the truth table need the real numbers.
5. **Metric rows per card** (how many limit windows each provider displays) — fixes
   popup height and whether cards ever scroll.
