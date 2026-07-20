namespace AIUsage.Core;

/// <summary>
/// Canonical classification of a usage window, decided from <see cref="UsageWindow.WindowMinutes"/>
/// by proximity to a known anchor — never by array position (DESIGN.md banner / §4.1).
/// </summary>
public enum WindowKind
{
    /// <summary>The ~5-hour window (anchor 300 minutes).</summary>
    FiveHour,

    /// <summary>The ~weekly window (anchor 10080 minutes = 7 × 24 × 60).</summary>
    Weekly,

    /// <summary>Any window not near a known anchor — carried additively with a generic label.</summary>
    Other,
}
