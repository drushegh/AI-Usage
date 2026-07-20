namespace AIUsageTray;

/// <summary>
/// Pure, UI-free validation and clamping for the owner-tunable settings (DESIGN.md §5, §7; tasks T41/T38).
/// <see cref="Clamp"/> repairs an already-materialised <see cref="AppConfig"/> so an out-of-range value
/// (a hand-edited config file) can never reach the display engine; <see cref="Validate"/> checks raw
/// user input from the Settings window and returns a human-readable error, or <c>null</c> when the values
/// are acceptable. Both are side-effect-free so they are unit-testable without a UI thread.
/// </summary>
public static class SettingsValidation
{
    /// <summary>Lowest allowed warning threshold (inclusive).</summary>
    public const decimal MinWarnPercent = 1m;

    /// <summary>Highest allowed warning threshold (inclusive) — it must leave room for a strictly-greater critical.</summary>
    public const decimal MaxWarnPercent = 99m;

    /// <summary>Highest allowed critical threshold (inclusive).</summary>
    public const decimal MaxCritPercent = 100m;

    /// <summary>Lowest allowed Codex "current" TTL, in minutes (inclusive).</summary>
    public const int MinTtlMinutes = 1;

    /// <summary>Highest allowed Codex "current" TTL, in minutes (inclusive) — 24h.</summary>
    public const int MaxTtlMinutes = 1440;

    /// <summary>
    /// Return <paramref name="config"/> with its tunable knobs clamped into range: warning into
    /// [<see cref="MinWarnPercent"/>, <see cref="MaxWarnPercent"/>], critical into
    /// [warning + 1, <see cref="MaxCritPercent"/>] (so critical is always strictly above warning), and the
    /// TTL into [<see cref="MinTtlMinutes"/>, <see cref="MaxTtlMinutes"/>]. Untunable fields are preserved.
    /// </summary>
    public static AppConfig Clamp(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        decimal warn = Math.Clamp(config.WarnPercent, MinWarnPercent, MaxWarnPercent);
        // warn <= 99 guarantees (warn + 1) <= 100 == MaxCritPercent, so the clamp bounds never invert.
        decimal crit = Math.Clamp(config.CritPercent, warn + 1m, MaxCritPercent);
        int ttl = Math.Clamp(config.CodexTtlMinutes, MinTtlMinutes, MaxTtlMinutes);

        return config with { WarnPercent = warn, CritPercent = crit, CodexTtlMinutes = ttl };
    }

    /// <summary>
    /// Validate raw Settings-window input. Returns <c>null</c> when 1 ≤ warn &lt; crit ≤ 100 and
    /// 1 ≤ ttl ≤ 1440; otherwise a single human-readable message naming the first failing constraint.
    /// </summary>
    public static string? Validate(decimal warnPercent, decimal critPercent, int ttlMinutes)
    {
        if (warnPercent < MinWarnPercent || warnPercent > MaxWarnPercent)
        {
            return $"Warning threshold must be between {MinWarnPercent:0} and {MaxWarnPercent:0}%.";
        }

        if (critPercent <= warnPercent || critPercent > MaxCritPercent)
        {
            return $"Critical threshold must be above the warning threshold and at most {MaxCritPercent:0}%.";
        }

        if (ttlMinutes < MinTtlMinutes || ttlMinutes > MaxTtlMinutes)
        {
            return $"Codex window must be between {MinTtlMinutes} and {MaxTtlMinutes} minutes.";
        }

        return null;
    }
}
