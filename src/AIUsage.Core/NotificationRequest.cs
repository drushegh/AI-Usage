namespace AIUsage.Core;

/// <summary>
/// One notification the UI should surface this tick — the pure, UI-free output of
/// <see cref="NotificationDecider"/> (DESIGN.md §7 Notifications). It carries only display-ready text
/// plus enough classification for the notifier to choose an icon; it does NOT know HOW it will be
/// shown. The production sink turns it into a WinForms balloon (the documented unpackaged-Win32
/// degrade, DESIGN.md §7/E6), but the decider stays testable with no UI in sight.
/// </summary>
/// <param name="Kind">Threshold crossing (T37) or provider transition (T36).</param>
/// <param name="Title">Short bold heading, e.g. "Claude Weekly at 90%".</param>
/// <param name="Text">Body line, e.g. "Resets in 6h 12m." or the transition reason.</param>
/// <param name="Severity">
/// The severity this notification represents — Warning/Critical for a threshold crossing (from the
/// unrounded percent), Warning for a provider transition. Drives the balloon's icon.
/// </param>
public sealed record NotificationRequest(
    NotificationKind Kind,
    string Title,
    string Text,
    Severity Severity);
