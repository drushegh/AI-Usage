namespace AIUsage.Core;

/// <summary>
/// A render-ready projection of one provider's card (DESIGN.md §7). Carries every window the
/// source currently reports plus any unreset DATED history, its credits/plan metrics (passed
/// through unchanged — still tri-state), and a per-provider severity/unknown roll-up the two-bar
/// icon form (§9) and the per-provider tooltip figures (§7) draw from. A provider card never
/// disappears: an <see cref="SourceStatus.Unavailable"/> source is still a full card of honest n/a.
/// </summary>
/// <param name="ProviderId">Stable provider identity ("codex", "claude").</param>
/// <param name="FetchedAt">
/// When the snapshot behind this card was last produced (<see cref="ProviderSnapshot.FetchedAt"/>).
/// Feeds the popup footer's "last refreshed N ago" age (DESIGN.md §7 footer) — an honest
/// whole-source refresh time that is present even when every window degrades to n/a (unlike a
/// per-window <c>ObservedAt</c>, which vanishes with the window's LIVE state).
/// </param>
/// <param name="Status">Whole-source status from the snapshot (the card renders either way).</param>
/// <param name="StatusReasonCode">Reason for a non-Ok status; <c>null</c> when Ok.</param>
/// <param name="Windows">Per-window views, ordered by window minute-count (5h before weekly) for a stable display.</param>
/// <param name="CreditsBalance">Provider credit balance — the snapshot's own tri-state metric, unchanged.</param>
/// <param name="PlanType">Provider plan/tier — the snapshot's own tri-state metric, unchanged.</param>
/// <param name="Severity">Highest severity among this provider's windows (LIVE severities + DATED monotone-floor).</param>
/// <param name="Unknown">
/// True when any of this provider's expected windows is not LIVE, or the source itself is
/// Unavailable — the per-provider "unknown badge" (DESIGN.md §7). Never let a provider read as
/// plain-safe while one of its windows is unknown.
/// </param>
/// <param name="AllUnknown">
/// True when NO window of this provider contributes a real severity signal (no LIVE window and no
/// DATED monotone-floor) — the provider is in the neutral "cannot tell" state, never "safe".
/// </param>
public sealed record ProviderView(
    string ProviderId,
    DateTimeOffset FetchedAt,
    SourceStatus Status,
    string? StatusReasonCode,
    IReadOnlyList<WindowView> Windows,
    Metric<decimal> CreditsBalance,
    Metric<string> PlanType,
    Severity Severity,
    bool Unknown,
    bool AllUnknown);
