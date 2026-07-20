using System.Drawing;
using System.Windows.Forms;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// Owns the WinForms <see cref="NotifyIcon"/> shown in the notification area and renders it from the
/// live <see cref="UsageView"/> (DESIGN.md §7; tasks T10/T11/T12/T16). It is a passive sink: the
/// <see cref="App"/> subscribes to the store, marshals to the WPF dispatcher, and calls
/// <see cref="Update"/> — all <see cref="NotifyIcon"/> mutation happens on that one UI thread.
/// </summary>
/// <remarks>
/// <b>GDI handle discipline (VISUAL-IDENTITY.md §6.4).</b> Each repaint converts a freshly drawn bitmap to
/// a raw <c>HICON</c> via <see cref="Bitmap.GetHicon"/>, wraps it with <see cref="Icon.FromHandle"/> (which
/// does NOT own the handle), then immediately clones that wrapper into an independent, self-owning
/// <see cref="Icon"/> — the raw temp handle is destroyed with <see cref="NativeMethods.DestroyIcon"/> in a
/// <c>finally</c> right after the clone, never left for later. Only the clone is ever assigned to
/// <see cref="NotifyIcon.Icon"/> or tracked in <see cref="_currentIcon"/>; the PREVIOUSLY displayed clone
/// is disposed only once the new one is live. See <see cref="SetIcon"/>.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    // Advisory balloon dwell (ms). Modern Windows ignores it and uses the system default, but the WinForms
    // API still requires a value; 10s is a reasonable legacy hint.
    private const int BalloonTimeoutMs = 10_000;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly int _iconSize;

    // After this many consecutive icon-render failures, stop showing a (now stale) last-good icon and degrade
    // to the honest track-only "?" state — a stale "safe" icon is worse than an explicit "cannot tell" (§7).
    private const int RenderFailureDegradeThreshold = 3;

    private Icon? _currentIcon;
    private int _consecutiveRenderFailures;
    private bool _disposed;

    /// <param name="onExit">Invoked when <c>Exit</c> is chosen (the caller stops the host and shuts down).</param>
    /// <param name="onRefresh">Invoked when <c>Refresh now</c> is chosen (the caller triggers an immediate rescan of BOTH providers).</param>
    /// <param name="onOpen">Invoked on a LEFT-click of the icon (or the "Open detail…" menu item) to toggle the detail popup (T14).</param>
    /// <param name="onToggleClaude">Invoked when the "Enable Claude usage" checkable item flips — carries the NEW checked state (T16/T32).</param>
    /// <param name="getClaudeEnabled">Reads the current Claude opt-in — used to seed the item AND re-sync its tick each time the menu opens (so a change made in the Settings window is reflected).</param>
    /// <param name="onSettings">Invoked when the "Settings…" item is chosen (the caller opens the single-instance Settings window; T41).</param>
    /// <param name="getStartWithWindows">Reads the current HKCU Run autostart state — seeds and re-syncs the "Start with Windows" item (T38).</param>
    /// <param name="onToggleStartWithWindows">Invoked when "Start with Windows" flips — carries the NEW checked state (T38).</param>
    public TrayIconController(
        Action onExit,
        Action onRefresh,
        Action onOpen,
        Action<bool> onToggleClaude,
        Func<bool> getClaudeEnabled,
        Action onSettings,
        Func<bool> getStartWithWindows,
        Action<bool> onToggleStartWithWindows)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        ArgumentNullException.ThrowIfNull(onRefresh);
        ArgumentNullException.ThrowIfNull(onOpen);
        ArgumentNullException.ThrowIfNull(onToggleClaude);
        ArgumentNullException.ThrowIfNull(getClaudeEnabled);
        ArgumentNullException.ThrowIfNull(onSettings);
        ArgumentNullException.ThrowIfNull(getStartWithWindows);
        ArgumentNullException.ThrowIfNull(onToggleStartWithWindows);

        _iconSize = ResolveIconSize();
        _menu = BuildMenu(
            onExit, onRefresh, onOpen,
            onToggleClaude, getClaudeEnabled,
            onSettings,
            getStartWithWindows, onToggleStartWithWindows);

        _notifyIcon = new NotifyIcon
        {
            // Visible == true is also what makes Explorer-restart resilience automatic (DESIGN.md §7; T39):
            // WinForms' NotifyIcon listens for the shell's "TaskbarCreated" broadcast (fired when Explorer
            // relaunches) and re-adds itself to the notification area — but ONLY while Visible is true and
            // the NotifyIcon has not been disposed. We therefore keep this one instance alive for the whole
            // process and only ever flip Visible=false + Dispose() on real Exit (see Dispose), so a killed/
            // restarted Explorer brings the icon back with no app restart. (Manual check: kill explorer.exe,
            // confirm the icon returns and the popup still opens — there is no automated Explorer-kill test.)
            Visible = true,
            Text = AppInfo.Name,
            ContextMenuStrip = _menu,
        };

        // Left-click opens/dismisses the detail popup (DESIGN.md §7 Popup; T14). Right-click keeps showing
        // the context menu (WinForms wires that automatically). This event fires on the UI thread that owns
        // the NotifyIcon, so the callback can touch the WPF popup directly.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                onOpen();
            }
        };

        // Nothing observed yet → track + badge only, never a plain "safe" icon (VISUAL-IDENTITY.md §4.5 R6).
        // Route the INITIAL paint through the SAME contained path as every later render (review NEW-1): a GDI
        // fault here (session-start / RDP is exactly when GDI throws) must not escape the ctor and, via the
        // App's startup backstop, leave a mutex-holding half-initialised zombie. A failed initial paint is
        // swallowed and simply retried on the first Update.
        _ = TryRenderAndSet(TrayIconState.NoData);
    }

    /// <summary>
    /// Repaint the icon and refresh the tooltip from the latest render model. MUST be called on the UI
    /// (dispatcher) thread — it mutates the <see cref="NotifyIcon"/>.
    /// </summary>
    public void Update(UsageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (_disposed)
        {
            return;
        }

        // A render throw (transient GDI fault under lock-screen / RDP / handle pressure) must NEVER propagate
        // to the dispatcher and take the tray down — a silently-absent monitor is the worst failure (§7;
        // review P1-4). On failure we keep the last-good icon for a transient blip, then degrade to the
        // honest track-only "?" state once failures persist so a stale "safe" icon can't mislead.
        var state = TrayIconState.Compute(view);
        if (TryRenderAndSet(state))
        {
            _consecutiveRenderFailures = 0;
        }
        else if (++_consecutiveRenderFailures >= RenderFailureDegradeThreshold)
        {
            _ = TryRenderAndSet(TrayIconState.NoData);
        }

        TrySetTooltip(view);
    }

    /// <summary>
    /// Force the neutral track-only "?" icon (VISUAL-IDENTITY.md §4.5 R6) defensively — the App's exception
    /// backstop calls this after repeated faults so a stale last-good icon can't keep reading as "safe".
    /// Never throws.
    /// </summary>
    public void ShowAllUnknown()
    {
        if (_disposed)
        {
            return;
        }

        _ = TryRenderAndSet(TrayIconState.NoData);
    }

    /// <summary>
    /// Render the icon for one state and push it to the tray, contained: any render/GDI fault is swallowed
    /// (logged token-free) and reported as <c>false</c> so the caller can keep the last-good icon or degrade,
    /// never letting a draw failure escape onto the dispatcher.
    /// </summary>
    private bool TryRenderAndSet(TrayIconState state)
    {
        try
        {
            var highContrast = SystemInformation.HighContrast;
            using var bitmap = IconRenderer.Render(state, _iconSize, highContrast);
            SetIcon(bitmap);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tray icon render failed: {ex.GetType().Name}");
            return false;
        }
    }

    private void TrySetTooltip(UsageView view)
    {
        try
        {
            _notifyIcon.Text = TrayTooltip.Build(view, TrayTooltip.MaxLength);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tray tooltip update failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Show a balloon over the tray icon (DESIGN.md §7 Notifications / task T40 first-run tip). This is the
    /// low-level mechanism <see cref="BalloonNotifier"/> and the App's first-run hint both call — see that
    /// notifier for the E6 mechanism rationale (why a WinForms balloon over a modern toast). MUST be called
    /// on the UI (dispatcher) thread. Best-effort: swallows any shell/timing fault so the notification path
    /// can never take the tray down.
    /// </summary>
    public void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed)
        {
            return;
        }

        try
        {
            // The timeout arg is advisory only on modern Windows (the shell decides the dwell); ShowBalloonTip
            // requires Visible==true, which we hold for the process lifetime.
            _notifyIcon.ShowBalloonTip(BalloonTimeoutMs, title, text, icon);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowBalloon failed: {ex}");
        }
    }

    /// <summary>
    /// Push a freshly drawn bitmap to the tray as an icon using the clone-owning dispose pattern
    /// (VISUAL-IDENTITY.md §6.4): <see cref="Bitmap.GetHicon"/> allocates a raw HICON the caller owns;
    /// <see cref="Icon.FromHandle"/> wraps it WITHOUT taking ownership, so the wrapper is cloned into an
    /// independent, self-owning <see cref="Icon"/> and the raw temp handle is destroyed immediately in a
    /// <c>finally</c> — the naive "assign the FromHandle wrapper, destroy the handle later" pattern leaks
    /// under an exception between those two steps. The clone is assigned to the tray, and only THEN is the
    /// PREVIOUSLY displayed clone disposed (its <c>Dispose()</c> genuinely releases its own GDI resources,
    /// unlike a bare <c>FromHandle</c> wrapper's no-op dispose).
    /// </summary>
    private void SetIcon(Bitmap bitmap)
    {
        nint tempHandle = bitmap.GetHicon();
        Icon owned;
        try
        {
            using var temp = Icon.FromHandle(tempHandle);
            owned = (Icon)temp.Clone();
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(tempHandle); // destroy the temp handle NOW, exception or not
        }

        var previous = _currentIcon;
        _notifyIcon.Icon = owned;
        _currentIcon = owned;

        previous?.Dispose();
    }

    private static ContextMenuStrip BuildMenu(
        Action onExit, Action onRefresh, Action onOpen,
        Action<bool> onToggleClaude, Func<bool> getClaudeEnabled,
        Action onSettings,
        Func<bool> getStartWithWindows, Action<bool> onToggleStartWithWindows)
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open detail…");
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold); // the default (left-click) action
        open.Click += (_, _) => onOpen();
        menu.Items.Add(open);

        var refresh = new ToolStripMenuItem("Refresh now");
        refresh.Click += (_, _) => onRefresh();
        menu.Items.Add(refresh);

        menu.Items.Add(new ToolStripSeparator());

        // Claude opt-in (T16/T32): a checkable toggle — checked = the Claude collector is enabled. CheckOnClick
        // flips the tick; the Click handler flips ClaudeProvider.Enabled, persists the flag, and refreshes.
        var claudeToggle = new ToolStripMenuItem("Enable Claude usage")
        {
            CheckOnClick = true,
            Checked = getClaudeEnabled(),
        };
        claudeToggle.Click += (sender, _) => onToggleClaude(((ToolStripMenuItem)sender!).Checked);
        menu.Items.Add(claudeToggle);

        // Start-at-sign-in (T38): a checkable toggle over the HKCU Run value. CheckOnClick flips the tick;
        // the Click handler writes/removes the registry value (best-effort, never fatal).
        var startupToggle = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = getStartWithWindows(),
        };
        startupToggle.Click += (sender, _) => onToggleStartWithWindows(((ToolStripMenuItem)sender!).Checked);
        menu.Items.Add(startupToggle);

        // Settings window (T41): opens the single-instance owner-tunable settings dialog.
        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => onSettings();
        menu.Items.Add(settings);

        // Re-sync the two checkable items from the source of truth every time the menu opens, so a change
        // made via the Settings window (Claude) or the registry directly (autostart) is always reflected —
        // never a stale tick.
        menu.Opening += (_, _) =>
        {
            claudeToggle.Checked = getClaudeEnabled();
            startupToggle.Checked = getStartWithWindows();
        };

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => onExit();
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>
    /// Render at the shell's small-icon size (DPI-scaled) so the icon is crisp at higher DPI rather than
    /// a 16 px bitmap upscaled by the shell. Never below the 16 px floor.
    /// </summary>
    private static int ResolveIconSize()
    {
        var side = Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height);
        return Math.Max(side, 16);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();

        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
