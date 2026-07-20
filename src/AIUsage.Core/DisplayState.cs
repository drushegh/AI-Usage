namespace AIUsage.Core;

/// <summary>
/// The render-time state of a single window figure — the display projection of the accuracy
/// contract (DESIGN.md §5). Every displayed number is in exactly one of these three states;
/// there is no fourth. Crucially there is NO estimated, interpolated, or carried-forward state:
/// a number is either a fresh authoritative observation (<see cref="Live"/>), a truthful
/// captioned historical value shown in a distinct area (<see cref="Dated"/>), or an explicit
/// non-value with a reason (<see cref="NA"/>).
/// </summary>
public enum DisplayState
{
    /// <summary>
    /// A fresh, authoritative, in-range observation — the ONLY state allowed to drive the tray
    /// icon, tooltip, and threshold toasts (DESIGN.md §5 LIVE). Requires the window metric be
    /// Available, within the freshness TTL, its <c>resets_at</c> not passed, and value in 0..100.
    /// </summary>
    Live,

    /// <summary>
    /// A retained last-known reading whose window has NOT yet reset — truthful history, rendered
    /// only in the visually distinct "Last known: X% at T" area (DESIGN.md §5 DATED). Never
    /// occupies a current-value row and never carries a live-looking countdown. Carries the
    /// observation time.
    /// </summary>
    Dated,

    /// <summary>
    /// Everything else — fetch failure, expiry past the TTL, reset boundary passed with no fresh
    /// reading, schema drift, out-of-range value, or a window the source does not report. Always
    /// carries a reason; rendered as an explicit "n/a" that can never be misread as 0% / safe
    /// (DESIGN.md §5 N-A). Reset-passed with no newer reading is N-A, NEVER zero.
    /// </summary>
    NA,
}
