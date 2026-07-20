using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// The thin, swappable seam between the pure <see cref="NotificationDecider"/> and however the OS
/// actually surfaces a notification (DESIGN.md §7). The production sink is <see cref="BalloonNotifier"/>
/// (a WinForms tray balloon); keeping the firing behind this interface means the mechanism can be
/// replaced later (modern toast, log sink, test spy) without touching the decider or the App wiring.
/// </summary>
public interface INotifier
{
    /// <summary>Surface one decided notification. Must never throw — a failed notification is not fatal.</summary>
    void Notify(NotificationRequest request);
}
