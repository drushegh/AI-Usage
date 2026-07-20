namespace AIUsage.Core;

/// <summary>
/// The owner-tunable knobs the display model reads (DESIGN.md §5, §7). Persistence of these
/// values is a later task (T6 / T41) — this record is only their in-memory shape plus a
/// sensible default.
/// </summary>
/// <param name="CodexCurrentTtl">
/// How long a Codex observation stays LIVE, measured <c>now − ObservedAt</c> (DESIGN.md §5 LIVE
/// rule 3). Default 20 minutes: "longer than your longest typical turn, short enough that unseen
/// usage (another device/session) stays bounded". Re-reading the same event never renews it —
/// liveness is anchored to the event's embedded <c>ObservedAt</c>, not to when it was read.
/// </param>
/// <param name="ClaudeCurrentTtl">
/// How long a Claude observation stays LIVE, measured <c>now − ObservedAt</c> (DESIGN.md §5 LIVE
/// rule 2). Default 210 seconds: the 180s poll cadence plus margin. A KNOWN failed refresh already
/// yields <c>Unavailable</c> from the provider (immediate invalidation, §5); this TTL only bounds a
/// stale-but-Ok reading. Much shorter than <see cref="CodexCurrentTtl"/> because Claude is polled on a
/// fixed remote cadence whereas Codex is event-driven off local files.
/// </param>
/// <param name="WarnPercent">Warning severity threshold (default 80). Compared against the UNROUNDED percent.</param>
/// <param name="CritPercent">Critical severity threshold (default 90). Compared against the UNROUNDED percent.</param>
public sealed record DisplayConfig(
    TimeSpan CodexCurrentTtl,
    TimeSpan ClaudeCurrentTtl,
    decimal WarnPercent,
    decimal CritPercent)
{
    /// <summary>
    /// The shipped defaults (DESIGN.md §5/§7, O3): 20-minute Codex TTL, 210-second Claude TTL,
    /// 80% warning, 90% critical.
    /// </summary>
    public static DisplayConfig Default { get; } = new(
        CodexCurrentTtl: TimeSpan.FromMinutes(20),
        ClaudeCurrentTtl: TimeSpan.FromSeconds(210),
        WarnPercent: 80m,
        CritPercent: 90m);
}
