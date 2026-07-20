namespace AIUsage.Core;

/// <summary>
/// One parsed, USABLE Codex <c>token_count</c> rate-limit event — the intermediate value the pure
/// <see cref="CodexSessionParser"/> produces from a single JSONL line before it is mapped to a
/// <see cref="ProviderSnapshot"/> (spike <c>e3-findings.md</c> §1). "Usable" is a precondition of
/// construction: a line only becomes a <see cref="CodexUsageEvent"/> when it has a valid embedded
/// <see cref="Timestamp"/> AND a non-null <see cref="Primary"/> window (a <c>primary:null</c>
/// "degenerate tail" event never becomes one — it is skipped, §e3 "6-step").
/// </summary>
/// <param name="Timestamp">
/// The per-line embedded <c>.timestamp</c> — UTC ISO-8601 with <c>Z</c> (e3-findings §2). This is the
/// authoritative observation time and the ONLY key used to select the newest event across candidates;
/// file mtime/name are LOCAL and must never be mixed into this UTC timeline.
/// </param>
/// <param name="Primary">The window carried by <c>rate_limits.primary</c> — always present and usable.</param>
/// <param name="Secondary">The window carried by <c>rate_limits.secondary</c> when present; <c>null</c> on weekly-only files.</param>
/// <param name="CreditsBalanceRaw">
/// The raw <c>credits.balance</c> string (e.g. <c>"0E-10"</c>) verbatim, or <c>null</c> when the
/// <c>credits</c> object or its <c>balance</c> is absent/null. Parsed downstream — never pre-rounded.
/// </param>
/// <param name="PlanType">The raw <c>plan_type</c> string (e.g. <c>"plus"</c>) or <c>null</c> when absent.</param>
public sealed record CodexUsageEvent(
    DateTimeOffset Timestamp,
    CodexWindowReading Primary,
    CodexWindowReading? Secondary,
    string? CreditsBalanceRaw,
    string? PlanType);

/// <summary>
/// One usage window read from a Codex <c>rate_limits</c> object (<c>primary</c> or <c>secondary</c>).
/// Position is NOT identity — <see cref="WindowMinutes"/> is (e3-findings §1a): in 2025 files
/// <c>primary</c> was the 5-hour window, in current files it is the weekly window. The reset instant
/// is resolved to an absolute value at parse time so callers never have to know which of the two
/// source encodings (<c>resets_at</c> epoch-seconds vs relative <c>resets_in_seconds</c>) was used.
/// </summary>
/// <param name="WindowMinutes">The source <c>window_minutes</c>, preserved verbatim (299/300 ≈ 5h, 10079/10080 ≈ weekly).</param>
/// <param name="UsedPercent">The source <c>used_percent</c> as an unrounded <see cref="decimal"/> on the 0–100 scale.</param>
/// <param name="ResetsAt">
/// The absolute reset instant, or <c>null</c> when neither reset encoding was present in the window.
/// Resolved from <c>resets_at</c> (unix epoch seconds) or, on older files, from
/// <c>event.timestamp + resets_in_seconds</c>.
/// </param>
public sealed record CodexWindowReading(
    int WindowMinutes,
    decimal UsedPercent,
    DateTimeOffset? ResetsAt);
