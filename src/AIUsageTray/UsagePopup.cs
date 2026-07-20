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
using SystemParameters = System.Windows.SystemParameters;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AIUsageTray;

/// <summary>
/// The tray detail popup (DESIGN.md §7 Popup; tasks T14/T15/T16/T19). A borderless, topmost, no-taskbar
/// WPF <see cref="Window"/> that opens on a left-click of the tray icon, anchors to the notification-area
/// corner of the work area, and dismisses itself on <see cref="Window.Deactivated"/> (focus lost) or Esc.
/// It renders one card per provider from the live <see cref="UsageView"/> and is refreshed in place when the
/// <see cref="App"/> pushes a new view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accuracy contract (§5).</b> The popup draws the <see cref="UsageView"/> verbatim and makes no
/// accuracy decisions of its own — every LIVE/DATED/NA distinction was already settled by
/// <see cref="UsageViewBuilder"/>. A LIVE window gets a bar, an unrounded-honest one-decimal %, an absolute
/// local reset time and a live countdown; a DATED window shows a visually distinct "Last known" area with
/// its own current row reading n/a and NO live countdown; an NA window shows an explicit n/a + reason and
/// never a fillable bar (so it can't be misread as 0%). Cards never disappear on failure.
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
    private const double PopupWidth = 340;
    private const double EdgeMargin = 12;
    private const double CardScrollMaxHeight = 640;

    private readonly TimeProvider _clock;
    private readonly Action _onRefresh;
    private readonly Action _onExit;
    private readonly DispatcherTimer _ticker;
    private readonly List<CountdownCell> _countdowns = new();
    private readonly List<AgeCell> _ages = new();

    private Palette _palette = null!;
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
    public UsagePopup(TimeProvider clock, Action onRefresh, Action onExit)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _onRefresh = onRefresh ?? throw new ArgumentNullException(nameof(onRefresh));
        _onExit = onExit ?? throw new ArgumentNullException(nameof(onExit));

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
        FontFamily = new FontFamily("Segoe UI");
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
        _isLight = light;
        _palette = Palette.ForTheme(light);
        _primaryButtonStyle = BuildFlatButtonStyle(_palette, primary: true);
        _secondaryButtonStyle = BuildFlatButtonStyle(_palette, primary: false);
        Background = _palette.WindowBg;

        var headerPanel = new StackPanel { Margin = new Thickness(16, 14, 16, 8) };
        headerPanel.Children.Add(Text("AI Usage", _palette.Text, 16, FontWeights.Bold));
        headerPanel.Children.Add(Text("Usage across providers", _palette.SubtleText, 11.5, FontWeights.Normal));

        _cardsHost = new StackPanel { Margin = new Thickness(16, 8, 16, 8) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = CardScrollMaxHeight,
            Content = _cardsHost,
        };

        var footer = BuildFooter();

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerPanel, Dock.Top);
        dock.Children.Add(headerPanel);
        var headerRule = new Border { Height = 1, Background = _palette.Border, Margin = new Thickness(16, 0, 16, 0) };
        DockPanel.SetDock(headerRule, Dock.Top);
        dock.Children.Add(headerRule);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);

        _root = new Border
        {
            BorderBrush = _palette.Border,
            BorderThickness = new Thickness(1),
            Background = _palette.WindowBg,
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

        var settingsBtn = MakeButton("Settings", primary: false, enabled: false, null);
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
        row.Margin = new Thickness(16, 10, 16, 14);

        var container = new StackPanel();
        container.Children.Add(new Border { Height = 1, Background = _palette.Border, Margin = new Thickness(16, 0, 16, 0) });
        container.Children.Add(row);
        return container;
    }

    // ---- provider card ------------------------------------------------------------------------------

    private FrameworkElement BuildCard(ProviderView provider, DateTimeOffset now)
    {
        var stack = new StackPanel();

        // Header: provider name + a colour-independent status label with a coloured dot.
        var name = Text(ProviderDisplayName(provider.ProviderId), _palette.Text, 15, FontWeights.Bold);
        stack.Children.Add(Row(name, BuildProviderStatus(provider)));

        // "updated N ago" — an honest whole-source refresh age that survives every window going n/a.
        var ageText = Text(string.Empty, _palette.SubtleText, 11, FontWeights.Normal);
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
            stack.Children.Add(Text(reason, _palette.SubtleText, 12, FontWeights.Normal, wrap: true, topMargin: 8));
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
            Background = _palette.CardBg,
            BorderBrush = _palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
        AutomationProperties.SetName(card, $"{ProviderDisplayName(provider.ProviderId)} usage card");
        return card;
    }

    private FrameworkElement BuildProviderStatus(ProviderView provider)
    {
        var (label, brush) = provider.AllUnknown
            ? ("no live data", _palette.Unknown)
            : provider.Severity switch
            {
                Severity.Critical => ("critical", _palette.Critical),
                Severity.Warning => ("warning", _palette.Warning),
                _ => provider.Unknown ? ("live · partial", _palette.Normal) : ("live", _palette.Normal),
            };

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        group.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        group.Children.Add(Text(label, _palette.SubtleText, 11.5, FontWeights.SemiBold));
        return group;
    }

    private FrameworkElement BuildWindowRow(WindowView window, DateTimeOffset now)
    {
        return window.DisplayState switch
        {
            DisplayState.Live => BuildLiveRow(window, now),
            DisplayState.Dated => BuildDatedRow(window, now),
            _ => BuildNaRow(window),
        };
    }

    private FrameworkElement BuildLiveRow(WindowView window, DateTimeOffset now)
    {
        // IsLive normally guarantees a percent. If a future model change ever hands us a LIVE window with no
        // percent, fall to the explicit n/a row rather than rendering a green 0% bar shown as current — the
        // exact implied-zero the HARD RULE bans (DESIGN.md §5, §9; review P2-10). The tooltip already does this.
        if (window.Percent is not { } percent)
        {
            return BuildNaRow(window);
        }

        var severityBrush = SeverityBrush(window.Severity);

        var label = Text(window.Label, _palette.Text, 13, FontWeights.SemiBold);
        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (window.Severity != Severity.Normal)
        {
            var pill = BuildPill(window.Severity == Severity.Critical ? "CRITICAL" : "WARNING", _palette.AccentFg, severityBrush);
            pill.Margin = new Thickness(0, 0, 8, 0);
            valuePanel.Children.Add(pill);
        }

        valuePanel.Children.Add(Text($"{UsageFormat.Percent(percent)}%", severityBrush, 15, FontWeights.Bold));

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Row(label, valuePanel));
        row.Children.Add(BuildBar((double)percent, severityBrush));

        if (window.ResetsAt.State == MetricState.Available && window.ResetsAt.Value is { } resetsAt)
        {
            var line = Text(string.Empty, _palette.SubtleText, 11.5, FontWeights.Normal, topMargin: 6);
            _countdowns.Add(new CountdownCell(line, UsageFormat.AbsoluteLocal(resetsAt, now), resetsAt));
            row.Children.Add(line);
        }
        else
        {
            row.Children.Add(Text("resets: n/a", _palette.SubtleText, 11.5, FontWeights.Normal, topMargin: 6));
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
            return BuildNaRow(window);
        }

        var observedAt = window.ObservedAt ?? now;

        var label = Text(window.Label, _palette.Text, 13, FontWeights.SemiBold);
        var naPill = BuildPill("n/a", _palette.SubtleText, _palette.Track);

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Row(label, RightAlign(naPill)));

        // The visually distinct "Last known" area (DESIGN.md §5 DATED). Never a live current row, never a
        // countdown — a truthful captioned historical reading only.
        var inner = new StackPanel();
        inner.Children.Add(Text("LAST KNOWN", _palette.SubtleText, 9.5, FontWeights.SemiBold));
        inner.Children.Add(Text(
            $"{window.Label} {UsageFormat.Percent(percent)}% — as of {UsageFormat.AbsoluteLocal(observedAt, now)}",
            _palette.Text, 12.5, FontWeights.Normal, wrap: true, topMargin: 2));

        row.Children.Add(new Border
        {
            Background = _palette.DatedBg,
            BorderBrush = _palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Child = inner,
        });

        AutomationProperties.SetName(row,
            $"{window.Label} current value not available. Last known {UsageFormat.Percent(percent)} percent as of {UsageFormat.AbsoluteLocal(observedAt, now)}");
        return row;
    }

    private FrameworkElement BuildNaRow(WindowView window)
    {
        var label = Text(window.Label, _palette.Text, 13, FontWeights.SemiBold);
        var naPill = BuildPill("n/a", _palette.SubtleText, _palette.Track);

        var reason = string.IsNullOrEmpty(window.ReasonCode)
            ? "unavailable"
            : UsageFormat.FriendlyReason(window.ReasonCode);

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Row(label, RightAlign(naPill)));
        // Deliberately NO fillable bar — an empty track reads as 0% / safe (DESIGN.md §5, §9).
        row.Children.Add(Text(reason, _palette.SubtleText, 11.5, FontWeights.Normal, wrap: true, topMargin: 4));

        AutomationProperties.SetName(row, $"{window.Label} not available, {reason}");
        return row;
    }

    private FrameworkElement BuildMetaBlock(ProviderView provider)
    {
        var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        block.Children.Add(MetaRow("Plan", MetricText(provider.PlanType, static v => v)));
        block.Children.Add(MetaRow("Credits", MetricText(provider.CreditsBalance, UsageFormat.Credits)));
        return block;
    }

    private FrameworkElement MetaRow(string name, (string text, bool available) value)
    {
        var left = Text(name, _palette.SubtleText, 12, FontWeights.Normal);
        var right = Text(value.text, value.available ? _palette.Text : _palette.SubtleText, 12,
            value.available ? FontWeights.SemiBold : FontWeights.Normal);
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

    private FrameworkElement BuildEmptyState()
    {
        var message = Text("Waiting for the first reading…", _palette.SubtleText, 13, FontWeights.Normal);
        message.HorizontalAlignment = HorizontalAlignment.Center;
        var panel = new StackPanel { Margin = new Thickness(4, 24, 4, 24) };
        panel.Children.Add(message);
        return panel;
    }

    // ---- small element helpers ----------------------------------------------------------------------

    private Border BuildBar(double percent, Brush fill)
    {
        double clamped = percent < 0 ? 0 : percent > 100 ? 100 : percent;

        var bar = new Grid { Height = 8, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(new Border { Background = _palette.Track, CornerRadius = new CornerRadius(4) });

        var fillHost = new Grid();
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clamped, GridUnitType.Star) });
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - clamped, GridUnitType.Star) });
        var fillBar = new Border { Background = fill, CornerRadius = new CornerRadius(4) };
        Grid.SetColumn(fillBar, 0);
        fillHost.Children.Add(fillBar);
        bar.Children.Add(fillHost);

        // Rounded container so the fill's right edge is clipped cleanly at low percentages.
        return new Border { CornerRadius = new CornerRadius(4), Child = bar, ClipToBounds = true };
    }

    private Border BuildPill(string text, Brush foreground, Brush background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
        },
    };

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

    private static FrameworkElement RightAlign(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Right;
        return element;
    }

    private Brush SeverityBrush(Severity severity) => severity switch
    {
        Severity.Critical => _palette.Critical,
        Severity.Warning => _palette.Warning,
        _ => _palette.Normal,
    };

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

    private static Style BuildFlatButtonStyle(Palette palette, bool primary)
    {
        Brush baseBg = primary ? palette.AccentBg : palette.ButtonBg;
        Brush hoverBg = primary ? palette.AccentHover : palette.ButtonHover;
        Brush pressBg = primary ? palette.AccentPressed : palette.ButtonPressed;
        Brush foreground = primary ? palette.AccentFg : palette.Text;

        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, baseBg);
        border.SetValue(Border.BorderBrushProperty, palette.Border);
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

        public CountdownCell(TextBlock target, string absolute, DateTimeOffset resetsAt)
        {
            _target = target;
            _absolute = absolute;
            _resetsAt = resetsAt;
        }

        public void Update(DateTimeOffset now)
        {
            var remaining = _resetsAt - now;
            _target.Text = remaining > TimeSpan.Zero
                ? $"resets {_absolute} · in {UsageFormat.Countdown(remaining)}"
                : $"resets {_absolute}";
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

    // ---- palette ------------------------------------------------------------------------------------

    /// <summary>
    /// The popup's colour set. Severity colours are always paired with a text label or the numeric % (never
    /// colour alone — DESIGN.md §7); the light/dark split is a bonus keyed off <c>AppsUseLightTheme</c>.
    /// </summary>
    private sealed class Palette
    {
        public readonly Brush WindowBg;
        public readonly Brush CardBg;
        public readonly Brush DatedBg;
        public readonly Brush Track;
        public readonly Brush Border;
        public readonly Brush Text;
        public readonly Brush SubtleText;
        public readonly Brush ButtonBg;
        public readonly Brush ButtonHover;
        public readonly Brush ButtonPressed;
        public readonly Brush AccentBg;
        public readonly Brush AccentHover;
        public readonly Brush AccentPressed;
        public readonly Brush AccentFg;
        public readonly Brush Normal;
        public readonly Brush Warning;
        public readonly Brush Critical;
        public readonly Brush Unknown;

        private Palette(bool light)
        {
            if (light)
            {
                WindowBg = B(0xFF, 0xFF, 0xFF);
                CardBg = B(0xF6, 0xF7, 0xF8);
                DatedBg = B(0xEE, 0xF1, 0xF4);
                Track = B(0xE4, 0xE7, 0xEB);
                Border = B(0xDD, 0xE1, 0xE6);
                Text = B(0x1F, 0x23, 0x28);
                SubtleText = B(0x5A, 0x60, 0x69);
                ButtonBg = B(0xF0, 0xF1, 0xF3);
                ButtonHover = B(0xE7, 0xE9, 0xEC);
                ButtonPressed = B(0xDA, 0xDD, 0xE1);
                AccentBg = B(0x1F, 0x6F, 0xEB);
                AccentHover = B(0x2C, 0x7B, 0xF5);
                AccentPressed = B(0x17, 0x5F, 0xD0);
                AccentFg = B(0xFF, 0xFF, 0xFF);
                Normal = B(0x1A, 0x7F, 0x37);
                Warning = B(0x9A, 0x67, 0x00);
                Critical = B(0xCF, 0x22, 0x2E);
                Unknown = B(0x6E, 0x77, 0x81);
            }
            else
            {
                WindowBg = B(0x1E, 0x1F, 0x22);
                CardBg = B(0x26, 0x28, 0x2C);
                DatedBg = B(0x2C, 0x2F, 0x35);
                Track = B(0x35, 0x38, 0x3D);
                Border = B(0x3A, 0x3D, 0x42);
                Text = B(0xE6, 0xE8, 0xEA);
                SubtleText = B(0x9A, 0xA0, 0xA7);
                ButtonBg = B(0x2F, 0x32, 0x37);
                ButtonHover = B(0x3A, 0x3E, 0x44);
                ButtonPressed = B(0x45, 0x49, 0x4F);
                AccentBg = B(0x2E, 0x6F, 0xE0);
                AccentHover = B(0x3D, 0x7C, 0xEC);
                AccentPressed = B(0x28, 0x5F, 0xC0);
                AccentFg = B(0xFF, 0xFF, 0xFF);
                Normal = B(0x3F, 0xB9, 0x50);
                Warning = B(0xE3, 0xA0, 0x08);
                Critical = B(0xF0, 0x44, 0x38);
                Unknown = B(0x8B, 0x92, 0x9B);
            }
        }

        public static Palette ForTheme(bool light) => new(light);

        private static Brush B(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
