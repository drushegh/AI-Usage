namespace AIUsage.Core;

/// <summary>
/// UI-free formatting of a remaining <see cref="TimeSpan"/> into a compact human span
/// ("2h 41m", "41m", "3d", "under a minute"). Lives in the core so the tray tooltip/popup
/// countdown (AIUsageTray <c>UsageFormat.Countdown</c>) and the threshold-notification text
/// (<see cref="NotificationDecider"/>) share ONE rounding rule rather than drifting apart.
/// </summary>
public static class DurationFormat
{
    /// <summary>
    /// A compact countdown for an authoritative reset span (DESIGN.md §5 / task T19), never a forecast.
    /// A non-positive span means the boundary has already passed — the caller must NOT show a countdown,
    /// so this returns <see cref="string.Empty"/> defensively rather than a bogus "0m".
    /// </summary>
    public static string Compact(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        int totalMinutes = (int)Math.Floor(remaining.TotalMinutes);
        if (totalMinutes < 1)
        {
            return "under a minute";
        }

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        if (hours >= 24)
        {
            int days = hours / 24;
            int remHours = hours % 24;
            return remHours > 0
                ? $"{days}d {remHours}h"
                : $"{days}d";
        }

        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }
}
