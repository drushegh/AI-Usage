namespace AIUsage.Core;

/// <summary>
/// The pure result of parsing one Claude <c>oauth/usage</c> response body
/// (<see cref="ClaudeUsageParser"/>; spike <c>e1-findings.md</c>, DESIGN.md §4.2/§5). It is a
/// PROVIDER-SHAPED intermediate — the (next task's) Claude provider turns it into a
/// <see cref="ProviderSnapshot"/> with a provider id and fetch time. It deliberately does NOT carry a
/// provider id, clock, or I/O of its own: the parser is a string-in/result-out function.
/// </summary>
/// <param name="Windows">
/// Every window the body currently reports, in source order, each carrying the base
/// <see cref="UsageWindow"/> plus the Claude-only server attributes (<c>is_active</c>, <c>severity</c>,
/// scope model). Empty on a drift result.
/// </param>
/// <param name="Credits">
/// The credit / extra-usage balance as its own independent metric: dollars from the E1-pinned
/// <c>spend.used</c> minor-unit math when spend is enabled, else <see cref="MetricState.NotApplicable"/>
/// (DESIGN.md §7 — shown "only when the response supplies value AND meaningful unit").
/// </param>
/// <param name="PlanType">
/// The plan / tier as its own independent metric. The <c>oauth/usage</c> body carries no plan field
/// today, so this is <see cref="MetricState.NotApplicable"/> "not-reported" unless a future body adds one.
/// </param>
/// <param name="IsDrift">
/// <c>true</c> when the envelope itself is untrustable — non-JSON, not an object, or carrying NEITHER a
/// usable <c>limits[]</c> entry NOR a usable <c>five_hour</c>. The provider maps a drift result to
/// ALL-Claude n/a (<see cref="DriftReason"/>). Individual missing/typed-wrong fields on an OTHERWISE
/// usable body are NOT drift — they degrade only their own metric.
/// </param>
/// <param name="DriftReason">The reason code for a drift result (always <c>"source-changed"</c>), else <c>null</c>.</param>
/// <param name="SchemaSignature">
/// An OPTIONAL, non-sensitive field-name/type signature of the top-level object (names + JSON kinds only,
/// NEVER values) — the input to the provider's "record a signature when new names/types appear" rule
/// (DESIGN.md §4.2). <c>null</c> when the body was not a JSON object. Response bodies are never returned
/// or logged; only this names-and-kinds fingerprint is.
/// </param>
public sealed record ClaudeUsageParseResult(
    IReadOnlyList<ClaudeUsageWindow> Windows,
    Metric<decimal> Credits,
    Metric<string> PlanType,
    bool IsDrift,
    string? DriftReason,
    string? SchemaSignature)
{
    /// <summary>
    /// The plain <see cref="UsageWindow"/> projection, in the same order — exactly what
    /// <see cref="ProviderSnapshot.Windows"/> needs. The Claude-only attributes on
    /// <see cref="ClaudeUsageWindow"/> (is-active / severity / scope) are dropped here; a caller that
    /// needs them reads <see cref="Windows"/> directly.
    /// </summary>
    public IReadOnlyList<UsageWindow> UsageWindows
    {
        get
        {
            var result = new UsageWindow[Windows.Count];
            for (var i = 0; i < Windows.Count; i++)
            {
                result[i] = Windows[i].Window;
            }

            return result;
        }
    }
}

/// <summary>
/// One Claude usage window: the base <see cref="UsageWindow"/> (the accuracy-contract shape the whole
/// app renders from) plus the extra attributes the Claude <c>limits[]</c> array carries that the base
/// model does not (spike <c>e1-findings.md</c> §3). These extras are cross-checks / display hints; the
/// authoritative figure is always <see cref="UsageWindow.UsedPercent"/>.
/// </summary>
/// <param name="Window">The base window (authoritative <c>WindowMinutes</c> identity, label, used-% and reset metrics).</param>
/// <param name="IsActive">
/// The server <c>is_active</c> flag. For a <c>weekly_scoped</c> per-model window this marks the model
/// currently in force (E1: the active scoped entry is "the real source for per-model display"). Carried,
/// never used to DROP a window — an inactive window still renders.
/// </param>
/// <param name="Severity">
/// The server-provided <c>severity</c> (<c>"normal"</c> | <c>"warning"</c> | <c>"critical"</c>, …) or
/// <c>null</c> when absent. A tolerated CROSS-CHECK only — the app computes its own severity from the
/// unrounded used-% for the HARD-RULE guarantee (DESIGN.md §7), never trusting this blindly.
/// </param>
/// <param name="ScopeModel">The <c>scope.model.display_name</c> for a per-model window (e.g. <c>"Fable"</c>), else <c>null</c>.</param>
public sealed record ClaudeUsageWindow(
    UsageWindow Window,
    bool IsActive,
    string? Severity,
    string? ScopeModel);
