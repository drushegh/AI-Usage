namespace AIUsage.Core;

/// <summary>
/// Which of the two notification classes (DESIGN.md §7 Notifications) produced a
/// <see cref="NotificationRequest"/>. Carried through to the UI so the balloon can pick an
/// appropriate icon and so the firing decision is testable without parsing prose.
/// </summary>
public enum NotificationKind
{
    /// <summary>
    /// A LIVE window crossed UP through the configured warning or critical threshold (T37). One per
    /// window per crossing; never re-fires until the window resets; only ever from a LIVE reading.
    /// </summary>
    Threshold,

    /// <summary>
    /// A provider went Ok → Unavailable and stayed that way past the dwell (T36) — one toast naming
    /// the provider and reason. Damped (a first flicker never toasts) and flap-suppressed.
    /// </summary>
    Transition,
}
