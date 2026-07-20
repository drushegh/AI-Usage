using System.Globalization;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// Pure, WPF-free display formatting shared by the tray tooltip (<see cref="TrayTooltip"/>) and the
/// popup detail window (<see cref="UsagePopup"/>). Centralising it keeps the one-decimal display rule,
/// the reason-code vocabulary, and the countdown/age wording identical everywhere the accuracy contract
/// surfaces text (DESIGN.md §5, §7). Side-effect-free so it is unit-testable without a UI thread.
/// </summary>
public static class UsageFormat
{
    /// <summary>
    /// The honest one-decimal display rule (DESIGN.md §5): the UNROUNDED value already drove every
    /// threshold decision upstream; this rounds only for display. "4" → <c>4</c>, "73.45" → <c>73.5</c>.
    /// </summary>
    public static string Percent(decimal value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>Credits balance, up to two decimals, trailing zeros trimmed ("12.4", "0", "3.25").</summary>
    public static string Credits(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Human-readable expansion of a machine reason code (DESIGN.md §5 N-A reasons). An unmapped code
    /// degrades gracefully to its hyphen-split words rather than being dropped — an n/a is never blank.
    /// </summary>
    public static string FriendlyReason(string reasonCode) => reasonCode switch
    {
        "no-recent-event" => "no recent event",
        "not-reported" => "not reported",
        "reset-passed" => "reset passed",
        "out-of-range" => "out of range",
        "source-changed" => "source changed",
        "no-sessions-dir" => "no sessions dir",
        "scale-unpinned" => "scale unpinned",
        "auth-rejected" => "authentication rejected",
        "throttled" => "throttled",
        "timeout" => "timeout",
        "fetch-error" => "fetch error",
        "refresh-error" => "refresh error",
        "gated" => "waiting",
        "disabled" => "disabled",
        "paused" => "paused",
        _ => string.IsNullOrWhiteSpace(reasonCode) ? "unavailable" : reasonCode.Replace('-', ' '),
    };

    /// <summary>
    /// A LIVE reset countdown (DESIGN.md §5 / task T19): a deterministic derivative of an authoritative
    /// <c>resets_at</c>, never a forecast. "2h 41m", "41m", or "under a minute". A non-positive remaining
    /// span means the boundary has already passed — the caller must NOT show a countdown in that case
    /// (this returns <see cref="string.Empty"/> defensively).
    /// </summary>
    public static string Countdown(TimeSpan remaining) => DurationFormat.Compact(remaining);

    /// <summary>
    /// A relative "last refreshed" age for the footer (DESIGN.md §7): "just now", "12s ago", "3m ago",
    /// "2h ago", "1d ago". A negative age (clock skew) reads as "just now".
    /// </summary>
    public static string RelativeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(5))
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return $"{(int)age.TotalSeconds}s ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }

    /// <summary>
    /// An absolute local wall-clock time for a reset or observation (DESIGN.md §7 "absolute local reset
    /// time" / DATED "as of T"). Same-day → "15:41"; a different day → "Tue 15:41". Rendered in the
    /// machine's local zone from the source's offset-aware timestamp.
    /// </summary>
    public static string AbsoluteLocal(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.ToLocalTime();
        var localNow = now.ToLocalTime();
        return local.Date == localNow.Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }
}
