using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIUsage.Core;

// This project enables UseWPF *and* UseWindowsForms, and ImplicitUsings pulls
// `System.Windows.Forms` + `System.Drawing` into global scope. That makes a fistful of
// simple type names ambiguous (WinForms/GDI vs WPF). Alias each one we use to the WPF type
// so the popup reads naturally without fully-qualifying every reference.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using SystemParameters = System.Windows.SystemParameters;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AIUsageTray;

/// <summary>
/// The tray detail popup (DESIGN.md §7 Popup; tasks T14/T15/T16/T19), styled to the "Twin-Stop Gauge"
/// visual identity (VISUAL-IDENTITY.md). A borderless, topmost, no-taskbar WPF <see cref="Window"/> that
/// opens on a left-click of the tray icon, anchors to the notification-area corner of the work area, and
/// dismisses itself on <see cref="Window.Deactivated"/> (focus lost) or Esc. It renders one card per
/// provider from the live <see cref="UsageView"/> and is refreshed in place when the <see cref="App"/>
/// pushes a new view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accuracy contract (VISUAL-IDENTITY.md §1/§4).</b> The popup draws the <see cref="UsageView"/>
/// verbatim and makes no accuracy decisions of its own — every LIVE/DATED/NA distinction was already
/// settled by <see cref="UsageViewBuilder"/>. A LIVE window gets a PROVIDER-coloured bar, an unmarked
/// bright numeral, an absolute local reset time and a live countdown; a DATED window gets the
/// provider's desaturated <c>*Dated</c> fill with a 45° hatch overlay, a demoted "Last known N%"
/// numeral that is never bright, and a dashed "as of T" chip with NO live countdown; an NA window shows
/// an empty dashed track (never a fillable bar — never an implied 0%), an em-dash, and a dashed "n/a"
/// chip + reason. Cards never disappear on failure; every colour is read through <see cref="Theme"/>,
/// never hard-coded.
/// </para>
/// <para>
/// <b>Threading (§4 / T14).</b> Everything here runs on the one WPF dispatcher thread. The popup does no
/// I/O and never blocks: <c>Refresh now</c> and <c>Exit</c> invoke the same async callbacks the tray menu
/// uses, and the 1-second <see cref="DispatcherTimer"/> only mutates already-created <see cref="TextBlock"/>
/// text (the countdowns and the last-refreshed ages) — no allocation-heavy re-layout on the tick.
/// </para>
/// </remarks>
public sealed class UsagePopup : Window
{
    private const double PopupWidth = 380; // VISUAL-IDENTITY.md §5.2: 380 DIP fixed.
    private const double EdgeMargin = 12;
    private const double CardScrollMaxHeight = 640;

    // Historic-severity / warn-crit glyphs (VISUAL-IDENTITY.md §3 severity, §4.4). Outline vs filled is
    // the shape channel that survives grayscale/CVD — colour alone never carries the distinction.
    private const string WarnGlyphOutline = "△"; // △ — LIVE warning marker; also the muted DATED "was …" marker.
    private const string CritGlyphFilled = "▲";  // ▲ — LIVE critical marker only (never used in DATED grammar).

    private static readonly Brush DatedHatchBrush = BuildDatedHatchBrush();

    private readonly TimeProvider _clock;
    private readonly Action _onRefresh;
    private readonly Action _onExit;
    private readonly Action _onSettings;
    private readonly DispatcherTimer _ticker;
    private readonly List<CountdownCell> _countdowns = new();
    private readonly List<AgeCell> _ages = new();

    private Style _primaryButtonStyle = null!;
    private Style _secondaryButtonStyle = null!;
    private StackPanel _cardsHost = null!;
    private Border? _root;
    private bool _isLight;
    private bool _closed;
    private long _lastDismissedTicks;

    /// <param name="clock">The one clock all countdowns and ages read "now" from (DESIGN.md §5 Countdowns).</param>
    /// <param name="onRefresh">The shared refresh path — the SAME callback the tray menu's "Refresh now" uses (T16).</param>
    /// <param name="onExit">The shared shutdown path — reuses the tray menu's Exit (T16).</param>
    /// <param name="onSettings">The shared "open Settings" path — the SAME callback the tray menu's Settings uses.</param>
    public UsagePopup(TimeProvider clock, Action onRefresh, Action onExit, Action onSettings)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _onRefresh = onRefresh ?? throw new ArgumentNullException(nameof(onRefresh));
        _onExit = onExit ?? throw new ArgumentNullException(nameof(onExit));
        _onSettings = onSettings ?? throw new ArgumentNullException(nameof(onSettings));

        Title = "AI Usage";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.Height;
        Width = PopupWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        // Sensible UIA identity for the whole flyout (Narrator announces this on focus — T14).
        AutomationProperties.SetName(this, "AI usage detail");

        _ticker = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => TickNow();

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnPreviewKeyDown;

        RebuildChrome(SystemTheme.IsLightAppTheme());
    }

    /// <summary>Whether this window instance is still usable (a real close, e.g. Alt+F4, retires it).</summary>
    public bool IsAlive => !_closed;

    /// <summary>
    /// Threshold-tick percentages (VISUAL-IDENTITY.md §5.4): drawn as 1-device-pixel slits over every bar
    /// fill (after the fill, so they can never be buried), and reused to compute the DATED "was
    /// Warning/Critical" historic-severity annotation (§4.4). <c>null</c> skips both — ticks and historic
    /// severity text simply don't render until the caller wires this up.
    /// </summary>
    public (decimal Warn, decimal Crit)? Thresholds { get; set; }

    /// <summary>
    /// The owner's manually-entered display gap-fillers (<see cref="AppConfig.UserFallbacks"/>) — a Plan
    /// label or the Codex weekly-reset schedule, rendered ONLY when the matching provider metric is n/a,
    /// always muted and attributed ("· set by you" / "· your setting"), never styled as LIVE
    /// (VISUAL-IDENTITY.md owner-feedback r4).
    /// </summary>
    public UserFallbacks Fallbacks { get; set; } = UserFallbacks.None;

    /// <summary>
    /// Open-or-dismiss toggle for the tray icon's left-click. Already open → dismiss. Just dismissed by
    /// the very click-away that this click represents → stay closed (the classic flyout re-open guard).
    /// Otherwise → position near the notification area and show.
    /// </summary>
    public void Toggle(UsageView view)
    {
        if (IsVisible)
        {
            Dismiss();
            return;
        }

        // The tray click that reaches here often arrives just after Deactivated already hid the popup.
        // Without this guard the same click would immediately reopen it.
        if (Environment.TickCount64 - _lastDismissedTicks < 350)
        {
            return;
        }

        ShowNear(view);
    }

    /// <summary>
    /// Refresh the open popup in place from a freshly built <see cref="UsageView"/> (the App pushes this on
    /// every <c>SnapshotChanged</c>). Rebuilds the provider cards and re-registers the countdown/age cells.
    /// MUST be called on the dispatcher thread.
    /// </summary>
    public void Update(UsageView view)
    {
        _countdowns.Clear();
        _ages.Clear();
        _cardsHost.Children.Clear();

        var now = _clock.GetUtcNow();
        if (view is null || view.Providers.Count == 0)
        {
            _cardsHost.Children.Add(BuildEmptyState());
        }
        else
        {
            foreach (var provider in view.Providers)
            {
                _cardsHost.Children.Add(BuildCard(provider, now));
            }
        }

        TickNow();
    }

    /// <summary>Close the window for real (app shutdown). Safe to call more than once.</summary>
    public void CloseForReal()
    {
        if (!_closed)
        {
            Close();
        }
    }

    /// <summary>
    /// Off-screen construction self-test (invoked only behind the <c>AIUSAGE_SELFTEST</c> guard — never on
    /// the shipping path). Exercises the full build/layout/position/tick path with a sample view so a
    /// binding or layout error surfaces at startup instead of on the user's first click.
    /// </summary>
    public void RunOffscreenSelfTest(UsageView view, string? pngPath = null)
    {
        var light = SystemTheme.IsLightAppTheme();
        if (_root is null || light != _isLight)
        {
            RebuildChrome(light);
        }

        Update(view);
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        PositionNearNotificationArea();
        TickNow();

        if (!string.IsNullOrEmpty(pngPath))
        {
            CapturePng(pngPath);
        }

        Hide();
    }

    /// <summary>
    /// Render the current content to a PNG (self-test / visual-verification aid only). Lets the render →
    /// look loop actually see the layout without a live desktop session.
    /// </summary>
    public void CapturePng(string path)
    {
        if (_root is null)
        {
            return;
        }

        _root.UpdateLayout();
        int width = (int)Math.Ceiling(_root.ActualWidth);
        int height = (int)Math.Ceiling(_root.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(_root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _ticker.Stop();
        base.OnClosed(e);
    }

    // ---- open / dismiss / position -------------------------------------------------------------------

    private void ShowNear(UsageView view)
    {
        var light = SystemTheme.IsLightAppTheme();
        if (_root is null || light != _isLight)
        {
            RebuildChrome(light);
        }

        Update(view);

        // Show off-screen first, force a layout pass so ActualWidth/Height are real, then anchor — this
        // avoids a one-frame flash at the wrong corner.
        Left = -32000;
        Top = -32000;
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionNearNotificationArea();
        Activate();
        Focus();
        _ticker.Start();
        TickNow();
    }

    private void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        _ticker.Stop();
        _lastDismissedTicks = Environment.TickCount64;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Anchor to the work area's bottom-right corner (above the taskbar), clamped fully on-screen.
    /// <see cref="SystemParameters.WorkArea"/> is the taskbar-excluded rectangle of the primary monitor in
    /// device-independent units — the PerMonitorV2 manifest (T14) keeps the rendered result crisp.
    /// </summary>
    private void PositionNearNotificationArea()
    {
        var work = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : PopupWidth;
        double h = ActualHeight > 0 ? ActualHeight : 0;

        double left = work.Right - w - EdgeMargin;
        double top = work.Bottom - h - EdgeMargin;

        // Keep the whole window inside the work area even if it is unusually tall or the bar is on a side.
        left = Clamp(left, work.Left + EdgeMargin, Math.Max(work.Left, work.Right - w - EdgeMargin));
        top = Clamp(top, work.Top + EdgeMargin, Math.Max(work.Top, work.Bottom - h - EdgeMargin));

        Left = left;
        Top = top;
    }

    private static double Clamp(double value, double min, double max)
        => max < min ? min : Math.Min(Math.Max(value, min), max);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible)
        {
            PositionNearNotificationArea();
        }
    }

    // ---- chrome (rebuilt only on first show / theme change) ------------------------------------------

    private void RebuildChrome(bool light)
    {
        // VISUAL-IDENTITY.md §1.7: dark-only at v1 — Theme carries a single dark palette, so `light` only
        // gates WHEN chrome rebuilds (theme-change detection), never which colours it draws.
        _isLight = light;
        _primaryButtonStyle = BuildFlatButtonStyle(primary: true);
        _secondaryButtonStyle = BuildFlatButtonStyle(primary: false);
        Background = Theme.WindowBg;

        var headerPanel = new StackPanel { Margin = new Thickness(12, 14, 12, 8) };
        headerPanel.Children.Add(Text("AI Usage", Theme.InkPrimary, 16, FontWeights.Bold));
        headerPanel.Children.Add(Text("Usage across providers", Theme.InkMuted, 11.5, FontWeights.Normal));

        _cardsHost = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        // The popup sizes to its content (SizeToContent.Height) and must NOT show a scrollbar for the
        // normal one/two-provider case. Cap the card region only at the screen's work area (minus room
        // for the header, footer and taskbar), so a scrollbar appears solely in the extreme event that
        // the content genuinely can't fit on screen — never for two cards.
        double cardCap = System.Math.Max(240, SystemParameters.WorkArea.Height - 140);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = cardCap,
            Content = _cardsHost,
        };

        var footer = BuildFooter();

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerPanel, Dock.Top);
        dock.Children.Add(headerPanel);
        var headerRule = new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(headerRule, Dock.Top);
        dock.Children.Add(headerRule);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);

        _root = new Border
        {
            BorderBrush = Theme.Hairline,
            BorderThickness = new Thickness(1),
            Background = Theme.WindowBg,
            Child = dock,
        };
        Content = _root;

        // Re-anchor whenever a live update grows or shrinks the content while the popup is open.
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;
    }

    private FrameworkElement BuildFooter()
    {
        var refreshBtn = MakeButton("Refresh now", primary: true, enabled: true, _onRefresh);

        var settingsBtn = MakeButton("Settings", primary: false, enabled: true, _onSettings);
        settingsBtn.Margin = new Thickness(0, 0, 8, 0);
        var exitBtn = MakeButton("Exit", primary: false, enabled: true, _onExit);

        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rightGroup.Children.Add(settingsBtn);
        rightGroup.Children.Add(exitBtn);

        var row = Row(refreshBtn, rightGroup);
        row.Margin = new Thickness(12, 10, 12, 14);

        var container = new StackPanel();
        container.Children.Add(new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(12, 0, 12, 0) });
        container.Children.Add(row);
        return container;
    }

    // ---- provider card ------------------------------------------------------------------------------

    private FrameworkElement BuildCard(ProviderView provider, DateTimeOffset now)
    {
        var stack = new StackPanel();
        stack.Children.Add(BuildProviderHeader(provider));

        // "updated N ago" — the one affirmative freshness signal (VISUAL-IDENTITY.md §4.3); an honest
        // whole-source refresh age that survives every window going n/a.
        var ageText = Text(string.Empty, Theme.InkMuted, 11, FontWeights.Normal, topMargin: 4);
        _ages.Add(new AgeCell(ageText, provider.FetchedAt));
        stack.Children.Add(ageText);

        if (provider.Windows.Count == 0)
        {
            // A disabled (opted-out) provider gets an explicit, actionable hint — never a bare "disabled"
            // that could read as a blank/implied-zero (T32). Any other empty state falls back to its reason.
            var reason = string.Equals(provider.StatusReasonCode, "disabled", StringComparison.Ordinal)
                ? $"n/a — {ProviderDisplayName(provider.ProviderId)} usage is off (enable in the menu)"
                : string.IsNullOrEmpty(provider.StatusReasonCode)
                    ? "no windows reported"
                    : $"n/a — {UsageFormat.FriendlyReason(provider.StatusReasonCode)}";
            stack.Children.Add(Text(reason, Theme.InkMuted, 12, FontWeights.Normal, wrap: true, topMargin: 8));
        }
        else
        {
            foreach (var window in provider.Windows)
            {
                stack.Children.Add(BuildWindowRow(window, now));
            }
        }

        stack.Children.Add(BuildMetaBlock(provider));

        var card = new Border
        {
            Background = Theme.CardBg,
            BorderBrush = Theme.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
        AutomationProperties.SetName(card, $"{ProviderDisplayName(provider.ProviderId)} usage card");
        return card;
    }

    /// <summary>
    /// Provider identity = the name with a 2px underline in the provider colour, exactly the name's width
    /// (VISUAL-IDENTITY.md §5.3) — no coloured status dot, no "live" label. LIVE is unmarked everywhere;
    /// severity (when present) rides on the row's value, not the header.
    /// </summary>
    private static FrameworkElement BuildProviderHeader(ProviderView provider)
    {
        var name = Text(ProviderDisplayName(provider.ProviderId), Theme.InkPrimary, 14, FontWeights.SemiBold);
        return new Border
        {
            BorderBrush = Theme.ProviderBrush(provider.ProviderId),
            BorderThickness = new Thickness(0, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = name,
        };
    }

    private FrameworkElement BuildWindowRow(WindowView window, DateTimeOffset now)
    {
        return window.DisplayState switch
        {
            DisplayState.Live => BuildLiveRow(window, now),
            DisplayState.Dated => BuildDatedRow(window, now),
            _ => BuildNaRow(window, now),
        };
    }

    private FrameworkElement BuildLiveRow(WindowView window, DateTimeOffset now)
    {
        // IsLive normally guarantees a percent. If a future model change ever hands us a LIVE window with no
        // percent, fall to the explicit n/a row rather than rendering a green 0% bar shown as current — the
        // exact implied-zero the HARD RULE bans (DESIGN.md §5, §9; review P2-10). The tooltip already does this.
        if (window.Percent is not { } percent)
        {
            return BuildNaRow(window, now);
        }

        var label = Text(window.Label, Theme.InkSecondary, 12, FontWeights.SemiBold);
        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (window.Severity != Severity.Normal)
        {
            // Quiet when healthy (VISUAL-IDENTITY.md §1/§4): only warn/crit ever mark a LIVE row, and the
            // accent lives on the glyph + word + numeral only — never on the bar fill.
            var severityBrush = Theme.SeverityBrush(window.Severity);
            var glyph = window.Severity == Severity.Critical ? CritGlyphFilled : WarnGlyphOutline;
            var word = window.Severity == Severity.Critical ? "Critical" : "Warning";
            var marker = Text($"{glyph} {word}", severityBrush, 11.5, FontWeights.SemiBold);
            marker.Margin = new Thickness(0, 0, 8, 0);
            valuePanel.Children.Add(marker);
            valuePanel.Children.Add(Text($"{UsageFormat.Percent(percent)}%", severityBrush, 15, FontWeights.Bold));
        }
        else
        {
            // LIVE is unmarked: a bright ink/primary numeral is the ONLY affirmative signal it carries
            // (VISUAL-IDENTITY.md §4.1) — the card's "updated N ago" line is the freshness claim.
            valuePanel.Children.Add(Text($"{UsageFormat.Percent(percent)}%", Theme.InkPrimary, 15, FontWeights.Bold));
        }

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Row(label, valuePanel));
        // Bars are PROVIDER-coloured, never severity-coloured — severity never recolours data (§1 rule 4).
        row.Children.Add(BuildBar((double)percent, Theme.ProviderBrush(window.ProviderId)));

        if (window.ResetsAt.State == MetricState.Available && window.ResetsAt.Value is { } resetsAt)
        {
            var line = Text(string.Empty, Theme.InkMuted, 11.5, FontWeights.Normal, topMargin: 6);
            _countdowns.Add(new CountdownCell(line, UsageFormat.AbsoluteLocal(resetsAt, now), resetsAt));
            row.Children.Add(line);
        }
        else
        {
            // Defensive path (a genuinely LIVE window always carries an Available ResetsAt) — still honours
            // the owner's Codex weekly-reset fallback if one applies, else the plain n/a caption.
            row.Children.Add(BuildCodexWeeklyResetFallback(window, now)
                ?? Text("resets: n/a", Theme.InkMuted, 11.5, FontWeights.Normal, topMargin: 6));
        }

        AutomationProperties.SetName(row, $"{window.Label} usage {UsageFormat.Percent(percent)} percent, live");
        return row;
    }

    private FrameworkElement BuildDatedRow(WindowView window, DateTimeOffset now)
    {
        // A DATED window without a retained percent has no honest "last known" figure to show — fall to the
        // n/a row rather than captioning a 0% (review P2-10).
        if (window.Percent is not { } percent)
        {
            return BuildNaRow(window, now);
        }

        var observedAt = window.ObservedAt ?? now;
        var label = Text(window.Label, Theme.InkSecondary, 12, FontWeights.SemiBold);

        // DATED numerals are never bright and never stand alone as an isolated large number
        // (VISUAL-IDENTITY.md §4.1) — "Last known N%", demoted weight, ink/secondary throughout.
        var baseText = $"Last known {UsageFormat.Percent(percent)}%";
        var valueText = baseText;
        if (Thresholds is { } t && percent >= t.Warn)
        {
            var word = percent >= t.Crit ? "Critical" : "Warning";
            // Historic severity stays strictly inside the DATED grammar: outline glyph, ink/secondary,
            // never the live capsule, never full-chroma severity, never a bright numeral (§4.4).
            valueText = $"{baseText} · {WarnGlyphOutline} was {word}";
        }

        var value = Text(valueText, Theme.InkSecondary, 13, FontWeights.SemiBold);
        value.HorizontalAlignment = HorizontalAlignment.Right;

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Row(label, value));
        // DATED fill = the provider's pre-composited dated tone + a 45° hatch overlay (never a raw alpha).
        row.Children.Add(BuildBar((double)percent, Theme.ProviderDatedBrush(window.ProviderId), DatedHatchBrush));

        var chip = BuildDashedChip($"as of {UsageFormat.AbsoluteLocal(observedAt, now)}", Theme.InkSecondary, Theme.InkMuted);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.Margin = new Thickness(0, 6, 0, 0);
        row.Children.Add(chip);

        // Owner-set Codex weekly-reset fallback — a DATED window's own reset is stale and deliberately not
        // shown here, so the owner's configured schedule fills the gap (muted + "· your setting"). This is the
        // case the owner hit: a DATED Codex weekly previously showed no reset countdown at all.
        var fallback = BuildCodexWeeklyResetFallback(window, now);
        if (fallback is not null)
        {
            row.Children.Add(fallback);
        }

        AutomationProperties.SetName(row,
            $"{window.Label} current value not available. Last known {UsageFormat.Percent(percent)} percent as of {UsageFormat.AbsoluteLocal(observedAt, now)}");
        return row;
    }

    private FrameworkElement BuildNaRow(WindowView window, DateTimeOffset now)
    {
        var label = Text(window.Label, Theme.InkSecondary, 12, FontWeights.SemiBold);

        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var emDash = Text("—", Theme.InkMuted, 15, FontWeights.Bold);
        emDash.Margin = new Thickness(0, 0, 8, 0);
        valuePanel.Children.Add(emDash);
        valuePanel.Children.Add(BuildDashedChip("n/a", Theme.InkSecondary, Theme.InkMuted));

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Row(label, valuePanel));
        // Deliberately no fill — an empty dashed track can never be misread as 0% / safe (§4.1, §9).
        row.Children.Add(BuildEmptyTrack());

        var reason = string.IsNullOrEmpty(window.ReasonCode)
            ? "unavailable"
            : UsageFormat.FriendlyReason(window.ReasonCode);
        row.Children.Add(Text(reason, Theme.InkMuted, 11.5, FontWeights.Normal, wrap: true, topMargin: 4));

        // Owner-set Codex weekly-reset fallback — only when the provider itself reported no reset.
        var fallback = BuildCodexWeeklyResetFallback(window, now);
        if (fallback is not null)
        {
            row.Children.Add(fallback);
        }

        AutomationProperties.SetName(row, $"{window.Label} not available, {reason}");
        return row;
    }

    /// <summary>
    /// The owner's manually-entered Codex weekly-reset schedule (<see cref="Fallbacks"/>), shown ONLY when
    /// the weekly window's own <c>ResetsAt</c> is not available — never overrides a provider-reported reset.
    /// Rendered in <see cref="Theme.InkMuted"/> with a live countdown to the owner's OWN configured time
    /// (deterministic, not a provider estimate), always tagged "· your setting" so it never reads as a
    /// provider-reported LIVE figure (owner-feedback r4).
    /// </summary>
    private FrameworkElement? BuildCodexWeeklyResetFallback(WindowView window, DateTimeOffset now)
    {
        // Skip ONLY when a current (LIVE) provider reset is actually being shown. A DATED window carries a
        // STALE reset (Available, but the DATED grammar deliberately never displays it, to avoid stale-as-
        // current), and an n-a window has none — in both cases there is no current reset on screen, so the
        // owner's own schedule fills the gap. (This is the case the owner hit: a DATED Codex weekly showed no
        // countdown because its stale reset was still "Available".)
        if (window.DisplayState == DisplayState.Live && window.ResetsAt.State == MetricState.Available)
        {
            return null;
        }

        if (!string.Equals(window.ProviderId, "codex", StringComparison.OrdinalIgnoreCase) ||
            WindowClassifier.Classify(window.WindowMinutes) != WindowKind.Weekly ||
            Fallbacks.CodexWeeklyReset is not { } reset)
        {
            return null;
        }

        // The owner set this schedule, so a countdown to it is exact + honest (not a provider estimate).
        // Register it as a live countdown cell — same ticking treatment as a provider reset — kept muted
        // and suffixed "· your setting" so it can never be mistaken for a provider-reported figure.
        var line = Text(string.Empty, Theme.InkMuted, 11.5, FontWeights.Normal, topMargin: 6);
        _countdowns.Add(new CountdownCell(line, UsageFormat.AbsoluteLocal(reset, now), reset, " · your setting"));
        return line;
    }

    private FrameworkElement BuildMetaBlock(ProviderView provider)
    {
        var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        block.Children.Add(BuildPlanRow(provider));
        block.Children.Add(MetaRow("Credits", MetricText(provider.CreditsBalance, UsageFormat.Credits)));
        return block;
    }

    /// <summary>
    /// The Plan meta row — falls back to the owner's manually-entered plan (<see cref="Fallbacks"/>) ONLY
    /// when the provider itself has not reported one, always attributed "· set by you" in
    /// <see cref="Theme.InkSecondary"/> (muted — never the bright "available" style; owner-feedback r4).
    /// </summary>
    private FrameworkElement BuildPlanRow(ProviderView provider)
    {
        var (text, available) = MetricText(provider.PlanType, static v => v);
        if (!available && Fallbacks.PlanFor(provider.ProviderId) is { } plan)
        {
            return MetaRowRaw("Plan", $"{plan} · set by you", Theme.InkSecondary, FontWeights.Normal);
        }

        return MetaRow("Plan", (text, available));
    }

    private static FrameworkElement MetaRow(string name, (string text, bool available) value)
        => MetaRowRaw(name, value.text, value.available ? Theme.InkPrimary : Theme.InkMuted,
            value.available ? FontWeights.SemiBold : FontWeights.Normal);

    private static FrameworkElement MetaRowRaw(string name, string text, Brush valueBrush, FontWeight weight)
    {
        var left = Text(name, Theme.InkSecondary, 12, FontWeights.Normal);
        var right = Text(text, valueBrush, 12, weight);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        var row = Row(left, right);
        row.Margin = new Thickness(0, 3, 0, 0);
        return row;
    }

    private static (string text, bool available) MetricText<T>(Metric<T> metric, Func<T, string> format)
    {
        if (metric.State == MetricState.Available && metric.Value is { } value)
        {
            return (format(value), true);
        }

        var reason = string.IsNullOrEmpty(metric.ReasonCode) ? null : UsageFormat.FriendlyReason(metric.ReasonCode);
        return (reason is null ? "n/a" : $"n/a ({reason})", false);
    }

    private static FrameworkElement BuildEmptyState()
    {
        var message = Text("Waiting for the first reading…", Theme.InkSecondary, 13, FontWeights.Normal);
        message.HorizontalAlignment = HorizontalAlignment.Center;
        var panel = new StackPanel { Margin = new Thickness(4, 24, 4, 24) };
        panel.Children.Add(message);
        return panel;
    }

    // ---- small element helpers ----------------------------------------------------------------------

    /// <summary>
    /// A bar instrument: <see cref="Theme.InsetBg"/> track, a provider-coloured (or DATED-toned + hatched)
    /// fill, and — when <see cref="Thresholds"/> is set — 1-device-pixel warn/crit ticks drawn AFTER the
    /// fill so it can never bury them (VISUAL-IDENTITY.md §5.4).
    /// </summary>
    private Border BuildBar(double percent, Brush fill, Brush? hatch = null)
    {
        double clamped = ClampPercent(percent);

        var bar = new Grid { Height = 8, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(new Border { Background = Theme.InsetBg, CornerRadius = new CornerRadius(4) });

        var fillHost = new Grid();
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clamped, GridUnitType.Star) });
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - clamped, GridUnitType.Star) });

        var fillBar = new Border { Background = fill, CornerRadius = new CornerRadius(4) };
        Grid.SetColumn(fillBar, 0);
        fillHost.Children.Add(fillBar);

        if (hatch is not null)
        {
            var hatchBar = new Border { Background = hatch, CornerRadius = new CornerRadius(4) };
            Grid.SetColumn(hatchBar, 0);
            fillHost.Children.Add(hatchBar);
        }

        bar.Children.Add(fillHost);

        if (Thresholds is { } thresholds)
        {
            bar.Children.Add(BuildTicks(thresholds));
        }

        // Rounded container so the fill's right edge is clipped cleanly at low percentages.
        return new Border { CornerRadius = new CornerRadius(4), Child = bar, ClipToBounds = true };
    }

    private static FrameworkElement BuildTicks((decimal Warn, decimal Crit) thresholds)
    {
        double warn = ClampPercent((double)thresholds.Warn);
        double crit = ClampPercent((double)thresholds.Crit);
        if (crit < warn)
        {
            (warn, crit) = (crit, warn);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(warn, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(crit - warn, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - crit, GridUnitType.Star) });

        var warnTick = BuildTickLine();
        Grid.SetColumn(warnTick, 1);
        grid.Children.Add(warnTick);

        var critTick = BuildTickLine();
        Grid.SetColumn(critTick, 2);
        grid.Children.Add(critTick);

        return grid;
    }

    private static Border BuildTickLine() => new()
    {
        Width = 1,
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = Theme.CardBg,
        SnapsToDevicePixels = true,
        UseLayoutRounding = true,
    };

    private static double ClampPercent(double value) => value < 0 ? 0 : value > 100 ? 100 : value;

    /// <summary>A dashed-outline chip (VISUAL-IDENTITY.md §4.1): <see cref="Theme.InkMuted"/> dashed border, text in <paramref name="textBrush"/>.</summary>
    private static FrameworkElement BuildDashedChip(string text, Brush textBrush, Brush dashBrush)
    {
        var outline = new Rectangle
        {
            Stroke = dashBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            RadiusX = 4,
            RadiusY = 4,
            SnapsToDevicePixels = true,
        };

        var label = new TextBlock
        {
            Text = text,
            Foreground = textBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 1, 6, 1),
        };

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(outline);
        grid.Children.Add(label);
        return grid;
    }

    /// <summary>An empty dashed track (VISUAL-IDENTITY.md §4.1 n/a) — no fill, so it can never read as 0%.</summary>
    private static FrameworkElement BuildEmptyTrack() => new Rectangle
    {
        Height = 8,
        Margin = new Thickness(0, 6, 0, 0),
        Stroke = Theme.InkMuted,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 2, 2 },
        RadiusX = 4,
        RadiusY = 4,
        SnapsToDevicePixels = true,
    };

    /// <summary>
    /// The 45° hatch overlay drawn over every DATED fill (VISUAL-IDENTITY.md §3/§5.4): a tiled
    /// <see cref="DrawingBrush"/>, 4×4 DIP absolute viewport, one diagonal line in <see cref="Theme.Hatch"/>.
    /// Built once and frozen — this is shape, not colour, so it survives grayscale/CVD.
    /// </summary>
    private static DrawingBrush BuildDatedHatchBrush()
    {
        var pen = new Pen(Theme.Hatch, 1.25);
        pen.Freeze();

        var geometry = new LineGeometry(new Point(0, 4), new Point(4, 0));
        geometry.Freeze();

        var drawing = new GeometryDrawing { Geometry = geometry, Pen = pen };
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 4, 4),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    private Button MakeButton(string text, bool primary, bool enabled, Action? onClick)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = enabled,
            MinWidth = 64,
            FontSize = 12.5,
            Style = primary ? _primaryButtonStyle : _secondaryButtonStyle,
        };
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        if (!enabled)
        {
            button.ToolTip = "Not configured yet";
        }

        AutomationProperties.SetName(button, text);
        return button;
    }

    private static TextBlock Text(string content, Brush brush, double size, FontWeight weight,
        bool wrap = false, double topMargin = 0)
        => new()
        {
            Text = content,
            Foreground = brush,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, topMargin, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>A two-column row: <paramref name="left"/> takes the remaining width, <paramref name="right"/> hugs the right edge.</summary>
    private static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static string ProviderDisplayName(string providerId) => providerId switch
    {
        "codex" => "Codex",
        "claude" => "Claude",
        _ => string.IsNullOrEmpty(providerId) ? providerId : char.ToUpperInvariant(providerId[0]) + providerId[1..],
    };

    private void TickNow()
    {
        var now = _clock.GetUtcNow();
        foreach (var cell in _countdowns)
        {
            cell.Update(now);
        }

        foreach (var cell in _ages)
        {
            cell.Update(now);
        }
    }

    // ---- flat button style (built once per theme) ---------------------------------------------------

    private static Style BuildFlatButtonStyle(bool primary)
    {
        Brush baseBg = primary ? Theme.Action : System.Windows.Media.Brushes.Transparent;
        Brush hoverBg = primary ? Theme.ActionHover : Theme.HoverBg;
        Brush pressBg = primary ? Theme.ActionPressed : Theme.PressedBg;
        Brush foreground = primary ? Theme.ActionOn : Theme.InkSecondary;

        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, baseBg);
        border.SetValue(Border.BorderBrushProperty, Theme.Hairline);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(primary ? 0 : 1));
        border.SetValue(Border.PaddingProperty, new Thickness(14, 7, 14, 7));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, pressBg, "bd"));
        template.Triggers.Add(pressed);

        var style = new Style(typeof(Button));
        // Button.TemplateProperty / Button.ForegroundProperty resolve to the inherited Control fields;
        // referenced via Button (aliased) to avoid the ambiguous bare `Control` (WinForms vs WPF).
        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        style.Setters.Add(new Setter(Button.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Arrow));
        style.Triggers.Add(disabled);

        return style;
    }

    // ---- live cells (updated on the 1s tick) --------------------------------------------------------

    /// <summary>A LIVE reset line: absolute local time plus a deterministic countdown (DESIGN.md §5 / T19).</summary>
    private sealed class CountdownCell
    {
        private readonly TextBlock _target;
        private readonly string _absolute;
        private readonly DateTimeOffset _resetsAt;
        private readonly string _suffix;

        public CountdownCell(TextBlock target, string absolute, DateTimeOffset resetsAt, string suffix = "")
        {
            _target = target;
            _absolute = absolute;
            _resetsAt = resetsAt;
            _suffix = suffix;
        }

        public void Update(DateTimeOffset now)
        {
            var remaining = _resetsAt - now;
            _target.Text = remaining > TimeSpan.Zero
                ? $"resets {_absolute} · in {UsageFormat.Countdown(remaining)}{_suffix}"
                : $"resets {_absolute}{_suffix}";
        }
    }

    /// <summary>A footer "updated N ago" cell (DESIGN.md §7 footer): ticks live off the snapshot's fetch time.</summary>
    private sealed class AgeCell
    {
        private readonly TextBlock _target;
        private readonly DateTimeOffset _fetchedAt;

        public AgeCell(TextBlock target, DateTimeOffset fetchedAt)
        {
            _target = target;
            _fetchedAt = fetchedAt;
        }

        public void Update(DateTimeOffset now) => _target.Text = $"updated {UsageFormat.RelativeAge(now - _fetchedAt)}";
    }
}
