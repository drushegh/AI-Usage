using System.Globalization;

namespace AIUsage.Core;

/// <summary>
/// One usage window as reported by a provider (DESIGN.md §3).
/// </summary>
/// <param name="WindowMinutes">
/// AUTHORITATIVE identity of the window (e.g. 300 ≈ 5h, 10080 ≈ weekly). Preserved
/// verbatim from the source. Windows are classified by proximity of this value
/// (see <see cref="WindowClassifier"/>), NEVER by array position or label.
/// </param>
/// <param name="Label">Display-only label derived from <paramref name="WindowMinutes"/> ("5h", "Weekly", generic "Nh").</param>
/// <param name="UsedPercent">
/// Percent used on the 0–100 scale (E1: never a 0–1 fraction), unrounded — source precision
/// preserved. Threshold comparisons use this unrounded value; display rounding is a separate concern.
/// </param>
/// <param name="ResetsAt">When this window next resets, as an authoritative timestamp.</param>
/// <param name="ScopeId">
/// OPTIONAL scope discriminator that disambiguates two windows sharing a <paramref name="WindowMinutes"/>.
/// <c>null</c> for a plain window (Codex, Claude <c>session</c>/<c>weekly_all</c>); the per-model scope for a
/// Claude <c>weekly_scoped</c> window (e.g. <c>"Fable"</c>, or <c>"scoped"</c> when the model name is absent).
/// It exists solely to feed <see cref="Key"/>.
/// </param>
public sealed record UsageWindow(
    int WindowMinutes,
    string Label,
    Metric<decimal> UsedPercent,
    Metric<DateTimeOffset> ResetsAt,
    string? ScopeId = null)
{
    /// <summary>
    /// The STABLE COMPOSITE identity used to key retained history, freshness classification, and
    /// notification arming (review P0-1). <see cref="WindowMinutes"/> alone is NOT unique: Claude reports
    /// both <c>weekly_all</c> and <c>weekly_scoped</c> at 10080 minutes, so keying subsystems on the bare
    /// minute-count cross-contaminates their DATED history, icon severity, and toast arming — the committed
    /// E1 payload has both live on day one. The key folds the scope in: a plain window keys by its
    /// minute-count (<c>"300"</c>, <c>"10080"</c>); a scoped window keys by <c>"{minutes}:{scope}"</c>
    /// (<c>"10080:Fable"</c>). Non-scoped windows therefore keep exactly their historical
    /// <c>(providerId, minutes)</c> identity — only the colliding weekly pair changes behaviour.
    /// </summary>
    public string Key => ScopeId is { Length: > 0 } scope
        ? $"{WindowMinutes.ToString(CultureInfo.InvariantCulture)}:{scope}"
        : WindowMinutes.ToString(CultureInfo.InvariantCulture);
}
