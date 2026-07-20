namespace AIUsage.Core;

/// <summary>
/// Icon / display severity for a usage figure (DESIGN.md §7). Derived ONLY from a
/// LIVE utilization or the monotone-floor of a DATED reading — never a prediction and
/// never from an n/a value. The three levels map to the disclosed, owner-configurable
/// policy: normal &lt; warn, warning ≥ warn, critical ≥ crit (defaults 80 / 90, compared
/// UNROUNDED — see <see cref="DisplayConfig"/>).
/// </summary>
/// <remarks>
/// The member order is load-bearing: <see cref="Normal"/> &lt; <see cref="Warning"/> &lt;
/// <see cref="Critical"/>, so the overall icon severity is the numeric MAX across windows.
/// "Unknown" is deliberately NOT a severity — an n/a region cannot be safe OR unsafe, so it
/// is carried as a separate flag (<see cref="UsageView.Unknown"/> / <see cref="UsageView.AllUnknown"/>)
/// rather than folded into this scale (DESIGN.md §7 unknown-state propagation).
/// </remarks>
public enum Severity
{
    /// <summary>Below the warning threshold — a genuinely safe LIVE reading.</summary>
    Normal,

    /// <summary>At or above the warning threshold (LIVE), or a DATED monotone-floor near-limit (DESIGN.md §5).</summary>
    Warning,

    /// <summary>At or above the critical threshold — reachable from a LIVE reading only (a DATED floor caps at <see cref="Warning"/>).</summary>
    Critical,
}
