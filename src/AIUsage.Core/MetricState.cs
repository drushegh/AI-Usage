namespace AIUsage.Core;

/// <summary>
/// The tri-state of a single displayed figure. There is deliberately no fourth state:
/// a metric is either a fresh authoritative observation (<see cref="Available"/>) or an
/// explicit non-value with a reason (<see cref="NotApplicable"/> / <see cref="Unavailable"/>).
/// No null, no default zero, no retained-previous-value is ever a legal substitute
/// (DESIGN.md §3, §5 — the owner's HARD RULE).
/// </summary>
public enum MetricState
{
    /// <summary>A validated, timestamped, currently-authoritative value is present.</summary>
    Available,

    /// <summary>
    /// The metric does not apply right now (e.g. a window the source is not reporting).
    /// Carries a reason; never a zero-valued <see cref="Available"/>.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// The metric applies but no trustworthy current value could be produced
    /// (fetch failure, expiry, schema drift, reset boundary passed, …). Carries a reason.
    /// </summary>
    Unavailable,
}
