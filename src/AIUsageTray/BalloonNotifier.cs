using System.Windows.Forms;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// The production <see cref="INotifier"/> (DESIGN.md §7 Notifications; task T35 / spike E6). It surfaces
/// each decided notification as a WinForms <see cref="NotifyIcon"/> balloon, via the
/// <see cref="TrayIconController"/> that owns the icon.
/// </summary>
/// <remarks>
/// <b>Mechanism decision (E6 — recorded here on purpose).</b> Modern Win11 toasts from an UNPACKAGED
/// Win32 app require registering an AppUserModelID, a Start-menu shortcut, and a COM activator; without
/// that ceremony the modern-toast APIs silently degrade to nothing (no error, no toast) — the exact
/// "silently becomes nothing" failure DESIGN.md §7 forbids. <see cref="NotifyIcon.ShowBalloonTip"/> is
/// the documented, reliable, zero-setup degrade: it needs only the tray icon we already own, and it
/// actually fires. We take it deliberately as the shipping mechanism, kept behind <see cref="INotifier"/>
/// so a registered modern-toast implementation can replace it later with no change to the decider.
/// </remarks>
public sealed class BalloonNotifier(TrayIconController tray) : INotifier
{
    private readonly TrayIconController _tray = tray ?? throw new ArgumentNullException(nameof(tray));

    public void Notify(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _tray.ShowBalloon(request.Title, request.Text, IconFor(request.Severity));
    }

    // Warning/Critical thresholds and provider outages both read as "attention" — Warning for the amber
    // near-limit band, Warning (the exclamation icon) for outages, Warning for critical too (WinForms only
    // offers None/Info/Warning/Error; Error's red cross overstates a usage cap, so Warning is the ceiling).
    private static ToolTipIcon IconFor(Severity severity) => severity switch
    {
        Severity.Critical => ToolTipIcon.Warning,
        Severity.Warning => ToolTipIcon.Warning,
        _ => ToolTipIcon.Info,
    };
}
