namespace AIUsage.Core;

/// <summary>
/// Maps a window's authoritative <c>window_minutes</c> to a canonical <see cref="WindowKind"/>
/// and a display label, purely by PROXIMITY to a known anchor (DESIGN.md banner / §4.1):
/// the collector reports windows in no guaranteed order and a 5h window can vanish, so a
/// window is identified by how close its minute-count sits to an anchor, never by position.
/// </summary>
public static class WindowClassifier
{
    /// <summary>Anchor for the ~5-hour window.</summary>
    public const int FiveHourMinutes = 300;

    /// <summary>Anchor for the ~weekly window (7 × 24 × 60).</summary>
    public const int WeeklyMinutes = 10080;

    // A window counts as "near" an anchor when within 10% of it. The two anchors are far
    // enough apart that their bands never overlap, so first-match order is unambiguous.
    // Integer math keeps classification exact: 300 → ±30 (270..330); 10080 → ±1008 (9072..11088).
    private const int TolerancePercent = 10;

    /// <summary>
    /// Classify a window by its minute-count. 299/300 → <see cref="WindowKind.FiveHour"/>,
    /// 10079/10080 → <see cref="WindowKind.Weekly"/>, anything not near an anchor → <see cref="WindowKind.Other"/>.
    /// </summary>
    public static WindowKind Classify(int windowMinutes)
    {
        if (IsNear(windowMinutes, FiveHourMinutes))
        {
            return WindowKind.FiveHour;
        }

        if (IsNear(windowMinutes, WeeklyMinutes))
        {
            return WindowKind.Weekly;
        }

        return WindowKind.Other;
    }

    /// <summary>
    /// A display-only label for a window: "5h" for the five-hour window, "Weekly" for the
    /// weekly window, otherwise a generic label ("24h" when the window is a whole number of
    /// hours, else "Nm"). The label is derived — <see cref="Classify"/> remains authoritative.
    /// </summary>
    public static string Label(int windowMinutes) => Classify(windowMinutes) switch
    {
        WindowKind.FiveHour => "5h",
        WindowKind.Weekly => "Weekly",
        _ => GenericLabel(windowMinutes),
    };

    private static bool IsNear(int minutes, int anchor)
        => Math.Abs(minutes - anchor) <= anchor * TolerancePercent / 100;

    private static string GenericLabel(int windowMinutes)
        => windowMinutes > 0 && windowMinutes % 60 == 0
            ? $"{windowMinutes / 60}h"
            : $"{windowMinutes}m";
}
