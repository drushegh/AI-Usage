using System.Globalization;

namespace AIUsage.Core;

/// <summary>
/// A render-ready projection of ONE usage window (DESIGN.md §5, §7): the UI draws this verbatim
/// and makes no accuracy decisions of its own. It is produced by <see cref="UsageViewBuilder"/>
/// from a <see cref="UsageWindow"/> + any retained <see cref="LastKnownReading"/> + the clock + a
/// <see cref="DisplayConfig"/>.
/// </summary>
/// <param name="ProviderId">Owning provider ("codex", "claude").</param>
/// <param name="WindowMinutes">Authoritative window identity (300 ≈ 5h, 10080 ≈ weekly), preserved verbatim.</param>
/// <param name="Label">Display label derived from <paramref name="WindowMinutes"/> ("5h", "Weekly", generic "Nh"/"Nm").</param>
/// <param name="DisplayState">Which of the three accuracy states this window is in (DESIGN.md §5).</param>
/// <param name="Percent">
/// The percent to show — present (non-null) for <see cref="DisplayState.Live"/> and
/// <see cref="DisplayState.Dated"/>, and always <c>null</c> for <see cref="DisplayState.NA"/> so
/// an n/a can never be drawn as a number. Unrounded (source precision); a display rounding rule
/// is the UI's concern and threshold comparisons already happened on this unrounded value.
/// </param>
/// <param name="ObservedAt">
/// When the shown value was observed — the "as of T" caption. Set for <see cref="DisplayState.Dated"/>
/// (mandatory) and for <see cref="DisplayState.Live"/> (the metric's observation time); <c>null</c>
/// for <see cref="DisplayState.NA"/>.
/// </param>
/// <param name="ResetsAt">
/// When this window next resets, as a tri-state metric. LIVE/NA pass the source window's own
/// <see cref="UsageWindow.ResetsAt"/> through; DATED carries the reset time as known at observation.
/// A DATED reading must NOT be rendered with a live countdown (DESIGN.md §5) — the
/// <see cref="DisplayState"/> is the signal the countdown engine (T19) keys on.
/// </param>
/// <param name="Severity">
/// The severity this window contributes to the icon (DESIGN.md §7). LIVE → from the unrounded
/// percent; DATED → monotone-floor (<see cref="Severity.Warning"/> iff percent ≥ warn, else
/// <see cref="Severity.Normal"/> — capped at Warning); NA → <see cref="Severity.Normal"/> (an n/a
/// carries no severity signal; its "unknown" is surfaced by <see cref="UsageView.Unknown"/>).
/// </param>
/// <param name="ReasonCode">
/// The machine-readable reason for a non-live state — present (non-null) ONLY for
/// <see cref="DisplayState.NA"/> ("no-recent-event", "reset-passed", "not-reported",
/// "out-of-range", "source-changed", …); <c>null</c> for Live/Dated.
/// </param>
/// <param name="WindowKey">
/// OPTIONAL composite window identity (see <see cref="UsageWindow.Key"/>) — what the notification decider
/// arms thresholds on, so Claude's two same-minute weekly windows arm independently. <c>null</c> falls back
/// to the bare minute-count (the legacy identity), which is exactly right for every non-scoped window.
/// </param>
public sealed record WindowView(
    string ProviderId,
    int WindowMinutes,
    string Label,
    DisplayState DisplayState,
    decimal? Percent,
    DateTimeOffset? ObservedAt,
    Metric<DateTimeOffset> ResetsAt,
    Severity Severity,
    string? ReasonCode,
    string? WindowKey = null)
{
    /// <summary>The composite window identity used for notification arming (falls back to the minute-count).</summary>
    public string Key => WindowKey is { Length: > 0 } key
        ? key
        : WindowMinutes.ToString(CultureInfo.InvariantCulture);
}
