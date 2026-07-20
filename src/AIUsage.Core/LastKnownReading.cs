using System.Globalization;

namespace AIUsage.Core;

/// <summary>
/// A retained, truthful historical reading that feeds ONLY the DATED "Last known" area
/// (DESIGN.md §3, §5). It lives in a store separate from the live snapshot so that
/// replacing a snapshot never destroys true history. A <see cref="LastKnownReading"/> is
/// never a current value: it never occupies a current-value row, never drives a
/// live-looking countdown, and is shown only while its window has not yet reset.
/// </summary>
/// <param name="ProviderId">Provider the reading came from.</param>
/// <param name="WindowMinutes">The window this reading belongs to (same authoritative identity as <see cref="UsageWindow.WindowMinutes"/>).</param>
/// <param name="UsedPercent">Percent used on the 0–100 scale, unrounded, exactly as observed.</param>
/// <param name="ObservedAt">When this reading was observed (the "as of T" the DATED area displays).</param>
/// <param name="ResetsAtAtObservation">The window's reset time as known at observation — used to suppress the reading once its window has reset.</param>
/// <param name="WindowKey">
/// OPTIONAL composite window identity (see <see cref="UsageWindow.Key"/>) the store keys on, so two
/// same-minute windows (Claude weekly_all vs weekly_scoped) retain independent history. <c>null</c> falls
/// back to the bare minute-count, preserving the legacy identity for plain windows.
/// </param>
/// <param name="Label">
/// OPTIONAL display label captured at observation, so a scoped window that later drops OUT of the snapshot
/// still renders under its own identity (e.g. "Fable wk") in the DATED area rather than falling back to the
/// generic "Weekly" and masquerading as the all-models weekly (review P0-1). <c>null</c> falls back to the
/// minute-count-derived label.
/// </param>
public sealed record LastKnownReading(
    string ProviderId,
    int WindowMinutes,
    decimal UsedPercent,
    DateTimeOffset ObservedAt,
    DateTimeOffset ResetsAtAtObservation,
    string? WindowKey = null,
    string? Label = null)
{
    /// <summary>The composite key this reading is stored under (falls back to the minute-count).</summary>
    public string Key => WindowKey is { Length: > 0 } key
        ? key
        : WindowMinutes.ToString(CultureInfo.InvariantCulture);
}
