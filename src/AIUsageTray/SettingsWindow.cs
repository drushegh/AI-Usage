using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// This project enables UseWPF *and* UseWindowsForms, and ImplicitUsings pulls `System.Windows.Forms` +
// `System.Drawing` into global scope, making several simple type names ambiguous (WinForms/GDI vs WPF).
// Alias each one this window uses to its WPF type — matching UsagePopup's approach.
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using SystemParameters = System.Windows.SystemParameters;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AIUsageTray;

/// <summary>
/// The values the user accepted in the <see cref="SettingsWindow"/> (raise on OK; never on Cancel).
/// The App applies these LIVE and persists them (DESIGN.md §7; tasks T41/T38; the plan/reset override
/// fields are the owner-set fallbacks, AppConfig.cs).
/// </summary>
/// <param name="WarnPercent">The chosen warning threshold (already validated 1 ≤ warn &lt; crit ≤ 100).</param>
/// <param name="CritPercent">The chosen critical threshold.</param>
/// <param name="CodexTtlMinutes">The chosen Codex "current" TTL, in minutes (validated 1..1440).</param>
/// <param name="ClaudeEnabled">Whether the Claude usage provider should be enabled.</param>
/// <param name="StartWithWindows">Whether AI-Usage should start at sign-in (the App writes the HKCU Run value).</param>
/// <param name="ClaudePlan">
/// The owner's Claude plan override, as typed (may be blank/whitespace — the App trims blank to
/// <c>null</c> when applying). Shown only when Claude hasn't reported a plan.
/// </param>
/// <param name="CodexPlan">The owner's Codex plan override, as typed (same blank-to-null handling).</param>
/// <param name="CodexWeeklyResetDay">The owner's Codex weekly-reset day override, or <c>null</c> if unset.</param>
/// <param name="CodexWeeklyResetTime">
/// The owner's Codex weekly-reset time override, already validated + normalised to <c>HH:mm</c> (or blank
/// if unset — the App trims blank to <c>null</c> when applying).
/// </param>
public sealed record SettingsResult(
    decimal WarnPercent,
    decimal CritPercent,
    int CodexTtlMinutes,
    bool ClaudeEnabled,
    bool StartWithWindows,
    string? ClaudePlan,
    string? CodexPlan,
    DayOfWeek? CodexWeeklyResetDay,
    string? CodexWeeklyResetTime);

/// <summary>
/// The owner-tunable Settings window (DESIGN.md §7; tasks T41/T38; retheme + overrides per
/// VISUAL-IDENTITY.md §5.5 — the "Twin-Stop Gauge" identity). A code-only WPF <see cref="Window"/> (no
/// XAML/BAML — matching <see cref="UsagePopup"/>) that edits the display thresholds, the Codex freshness
/// TTL, the Claude opt-in, the "start at sign-in" registration, and the owner-set plan/weekly-reset
/// fallbacks (<see cref="AppConfig.ClaudePlan"/>, <see cref="AppConfig.CodexPlan"/>,
/// <see cref="AppConfig.CodexWeeklyResetDay"/>, <see cref="AppConfig.CodexWeeklyResetTime"/>). Dark-only
/// (VISUAL-IDENTITY.md §1 rule 7) — every colour comes from <see cref="Theme"/>, never a hard-coded hex; it
/// is fully keyboard-navigable (Tab order, Enter = OK, Esc = Cancel), and every input carries an
/// <see cref="AutomationProperties"/> name for Narrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apply/discard.</b> OK validates via <see cref="SettingsValidation.Validate"/> plus the optional
/// <c>HH:mm</c> weekly-reset time; on success it raises the <see cref="SettingsResult"/> callback (the App
/// applies + persists) and closes. On a validation failure it shows an inline message and keeps the window
/// open. Cancel (and Esc) close without raising — nothing is applied.
/// </para>
/// <para>
/// <b>Threading.</b> Built and shown on the one WPF dispatcher thread. It does no I/O and never blocks —
/// the registry read for the initial checkbox state happens in the App before construction and is passed
/// in as a plain <c>bool</c>, so this window has zero registry coupling.
/// </para>
/// </remarks>
public sealed class SettingsWindow : Window
{
    private const double ContentWidth = 440;

    private readonly Action<SettingsResult> _onApply;
    private readonly TextBox _warnBox;
    private readonly TextBox _critBox;
    private readonly TextBox _ttlBox;
    private readonly CheckBox _claudeCheck;
    private readonly CheckBox _startupCheck;
    private readonly TextBox _claudePlanBox;
    private readonly TextBox _codexPlanBox;
    private readonly ComboBox _codexResetDayCombo;
    private readonly TextBox _codexResetTimeBox;
    private readonly TextBlock _errorText;
    private bool _closedForReal;

    /// <summary>Marks a TextBox's <see cref="FrameworkElement.Tag"/> as failing validation (see <see cref="BuildTextBoxTemplate"/>).</summary>
    private const string InvalidTag = "invalid";

    /// <param name="config">The current persisted config — seeds the threshold / TTL / Claude / override inputs.</param>
    /// <param name="startWithWindows">The current HKCU Run autostart state (read by the App) — seeds that checkbox.</param>
    /// <param name="onApply">Invoked with the accepted values on OK (never on Cancel).</param>
    public SettingsWindow(AppConfig config, bool startWithWindows, Action<SettingsResult> onApply)
    {
        ArgumentNullException.ThrowIfNull(config);
        _onApply = onApply ?? throw new ArgumentNullException(nameof(onApply));

        Title = "AI-Usage Settings";
        WindowStyle = WindowStyle.SingleBorderWindow; // title bar + close button — a real dialog, movable
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.Height;
        Width = ContentWidth + 2; // + the 1px border on each side
        MinHeight = 480; // VISUAL-IDENTITY.md §5.5 "~420x480 DIP minimum"
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); // §5.1 typography (Win10 falls back automatically)
        FontSize = 13;
        Background = Theme.WindowBg;
        AutomationProperties.SetName(this, "AI-Usage settings");

        try
        {
            // The brand mark (VISUAL-IDENTITY.md §2.4/§6.3) — bundled as a WPF Resource in the csproj.
            // Cosmetic only: a missing/broken packed resource must never crash the settings window.
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/AiUsage.ico"));
        }
        catch (Exception)
        {
            // Title bar simply shows no icon.
        }

        SourceInitialized += (_, _) => DwmInterop.TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);

        _warnBox = NumberBox(FormatDecimal(config.WarnPercent), "Warning threshold percent");
        _critBox = NumberBox(FormatDecimal(config.CritPercent), "Critical threshold percent");
        _ttlBox = NumberBox(config.CodexTtlMinutes.ToString(CultureInfo.CurrentCulture), "Codex current window in minutes");

        _claudeCheck = Check("Enable Claude usage", config.ClaudeEnabled, "Enable Claude usage");
        _startupCheck = Check("Start AI-Usage when I sign in", startWithWindows, "Start AI-Usage when I sign in");

        _claudePlanBox = PlainTextBox(170, config.ClaudePlan, "Claude plan override, optional");
        _codexPlanBox = PlainTextBox(170, config.CodexPlan, "Codex plan override, optional");
        _codexResetDayCombo = DayCombo(config.CodexWeeklyResetDay, "Codex weekly reset day, optional");
        _codexResetTimeBox = PlainTextBox(64, config.CodexWeeklyResetTime, "Codex weekly reset time, 24 hour HH colon mm, optional");
        _codexResetTimeBox.TextAlignment = TextAlignment.Center;

        _errorText = new TextBlock
        {
            Foreground = Theme.SevCrit,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(_errorText, AutomationLiveSetting.Assertive);

        Content = BuildContent();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            _warnBox.Focus();
            _warnBox.SelectAll();
        };
    }

    /// <summary>Close the window for real (OK, Cancel, or app shutdown). Safe to call more than once.</summary>
    public void CloseForReal()
    {
        if (!_closedForReal)
        {
            Close();
        }
    }

    /// <summary>
    /// Off-screen construction self-test (invoked only behind the <c>AIUSAGE_SELFTEST=settings</c> guard —
    /// never on the shipping path). Forces the full build/layout path so a layout error surfaces at startup
    /// instead of on the owner's first click.
    /// </summary>
    public void RunOffscreenSelfTest()
    {
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();

        // Verification aid (never on the shipping path): dump the rendered content to PNG when asked, so the
        // restyle can be eyeballed off-screen. Renders the content root (WPF can't capture the OS title bar).
        var pngPath = Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST_PNG");
        if (!string.IsNullOrEmpty(pngPath) && Content is System.Windows.FrameworkElement root && root.ActualWidth > 0)
        {
            try
            {
                int w = (int)System.Math.Ceiling(root.ActualWidth);
                int h = (int)System.Math.Ceiling(root.ActualHeight);
                var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                target.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
                using var stream = System.IO.File.Create(pngPath);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings PNG capture failed: {ex.GetType().Name}");
            }
        }

        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closedForReal = true;
        base.OnClosed(e);
    }

    // ---- layout -------------------------------------------------------------------------------------

    private UIElement BuildContent()
    {
        var header = new StackPanel { Margin = new Thickness(20, 18, 20, 4) };
        header.Children.Add(Text("Settings", Theme.InkPrimary, 17, FontWeights.Bold));
        header.Children.Add(Text("Tune thresholds, freshness, overrides, and startup", Theme.InkMuted, 12, FontWeights.Normal, topMargin: 2));

        var sections = new StackPanel { Margin = new Thickness(20, 12, 20, 12) };

        sections.Children.Add(SectionHeader("Alert thresholds", leadingRule: false));
        sections.Children.Add(FieldRow("Warning at", _warnBox, "%"));
        sections.Children.Add(FieldRow("Critical at", _critBox, "%"));
        sections.Children.Add(Hint("Warning must be below critical. Both compared against the exact, unrounded usage."));

        sections.Children.Add(SectionHeader("Codex freshness"));
        sections.Children.Add(FieldRow("Current window", _ttlBox, "min"));
        sections.Children.Add(Hint("How long a Codex reading counts as live — longer than your typical session gap."));

        sections.Children.Add(SectionHeader("Providers"));
        sections.Children.Add(_claudeCheck);
        sections.Children.Add(Hint("Uses an undocumented endpoint (ToS-grey); reads only your own account."));

        sections.Children.Add(SectionHeader("Startup"));
        sections.Children.Add(_startupCheck);

        sections.Children.Add(SectionHeader("Overrides (shown only when the provider hasn't reported)"));
        sections.Children.Add(FieldRow("Claude plan", _claudePlanBox, string.Empty));
        sections.Children.Add(FieldRow("Codex plan", _codexPlanBox, string.Empty));
        sections.Children.Add(ComboTimeRow("Codex weekly reset", _codexResetDayCombo, _codexResetTimeBox));
        sections.Children.Add(Hint("Used only to fill gaps the provider hasn't reported — shown as your setting, never as live data."));

        var scroll = new ScrollViewer
        {
            // The bespoke flat thumb below drives scrolling visually; the native bar stays hidden (not
            // disabled — mouse wheel / keyboard paging still work, only its own chrome is suppressed).
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Cap the scrollable region to the work area (minus room for header/footer/title bar) so the
            // window never grows off-screen; below that cap it just sizes to content (no bar shown).
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 260),
            Content = sections,
            Focusable = false,
        };

        var scrollHost = new Grid();
        scrollHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrollHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(scroll, 0);
        var flatScrollBar = BuildFlatScrollBar(scroll);
        Grid.SetColumn(flatScrollBar, 1);
        scrollHost.Children.Add(scroll);
        scrollHost.Children.Add(flatScrollBar);

        var headerRule = new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(20, 10, 20, 0) };

        var footer = new StackPanel { Margin = new Thickness(20, 8, 20, 18) };
        footer.Children.Add(_errorText);
        footer.Children.Add(BuildButtonRow());

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        DockPanel.SetDock(headerRule, Dock.Top);
        dock.Children.Add(headerRule);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scrollHost);

        return new Border
        {
            BorderBrush = Theme.Hairline,
            BorderThickness = new Thickness(1),
            Background = Theme.WindowBg,
            Child = dock,
        };
    }

    private FrameworkElement BuildButtonRow()
    {
        var cancel = MakeButton("Cancel", primary: false, () => CloseForReal());
        cancel.IsCancel = true;
        cancel.MinWidth = 84;
        cancel.Margin = new Thickness(0, 0, 8, 0);

        var ok = MakeButton("OK", primary: true, OnOk);
        ok.IsDefault = true;
        ok.MinWidth = 84;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        return row;
    }

    /// <summary>A section title (14 semibold ink/primary, VISUAL-IDENTITY.md §5.5) with a leading hairline separator.</summary>
    private static FrameworkElement SectionHeader(string title, bool leadingRule = true)
    {
        var panel = new StackPanel();
        if (leadingRule)
        {
            panel.Children.Add(new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(0, 20, 0, 0) });
        }

        panel.Children.Add(Text(title, Theme.InkPrimary, 14, FontWeights.SemiBold, topMargin: leadingRule ? 16 : 0, wrap: true));
        return panel;
    }

    /// <summary>A label / input / unit row: label takes the remaining width, the input + unit hug the right.</summary>
    private static FrameworkElement FieldRow(string label, TextBox input, string unit)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = Text(label, Theme.InkSecondary, 13, FontWeights.Normal);
        labelText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(labelText, 0);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(input);
        if (!string.IsNullOrEmpty(unit))
        {
            var unitText = Text(unit, Theme.InkMuted, 12.5, FontWeights.Normal);
            unitText.Margin = new Thickness(6, 0, 0, 0);
            unitText.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(unitText);
        }

        Grid.SetColumn(right, 1);

        grid.Children.Add(labelText);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>The composite "Codex weekly reset" row: a day-of-week combo + an HH:mm time box, right-aligned.</summary>
    private static FrameworkElement ComboTimeRow(string label, ComboBox dayCombo, TextBox timeBox)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = Text(label, Theme.InkSecondary, 13, FontWeights.Normal);
        labelText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(labelText, 0);

        dayCombo.Width = 130;
        dayCombo.Margin = new Thickness(0, 0, 6, 0);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(dayCombo);
        right.Children.Add(timeBox);
        Grid.SetColumn(right, 1);

        grid.Children.Add(labelText);
        grid.Children.Add(right);
        return grid;
    }

    private static FrameworkElement Hint(string text)
        => Text(text, Theme.InkMuted, 11.5, FontWeights.Normal, wrap: true, topMargin: 4);

    // ---- apply --------------------------------------------------------------------------------------

    private void OnOk()
    {
        _codexResetTimeBox.Tag = null;

        if (!TryParseDecimal(_warnBox.Text, out var warn) ||
            !TryParseDecimal(_critBox.Text, out var crit) ||
            !TryParseInt(_ttlBox.Text, out var ttl))
        {
            ShowError("Enter numbers only for the thresholds and the Codex window.");
            return;
        }

        var problem = SettingsValidation.Validate(warn, crit, ttl);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        // Codex weekly reset time: optional HH:mm (empty is fine — unset). A non-empty value that doesn't
        // parse is rejected inline and the field is marked invalid (VISUAL-IDENTITY.md §5.5 validation).
        // The same lenient TimeOnly.TryParse AppConfig.NextCodexWeeklyReset uses, so anything accepted here
        // is guaranteed parseable later — normalised to canonical HH:mm either way.
        string resetTimeInput = _codexResetTimeBox.Text.Trim();
        string resetTime = string.Empty;
        if (resetTimeInput.Length > 0)
        {
            if (!TimeOnly.TryParse(resetTimeInput, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                _codexResetTimeBox.Tag = InvalidTag;
                ShowError("Codex weekly reset time must be a valid time, like 09:00 or 17:30.");
                return;
            }

            resetTime = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        var resetDay = (_codexResetDayCombo.SelectedItem as DayOption?)?.Day;

        _onApply(new SettingsResult(
            warn,
            crit,
            ttl,
            _claudeCheck.IsChecked == true,
            _startupCheck.IsChecked == true,
            _claudePlanBox.Text,
            _codexPlanBox.Text,
            resetDay,
            resetTime));
        CloseForReal();
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc cancels. (IsCancel on the Cancel button also routes Esc, but handle it explicitly so the
        // behaviour is guaranteed regardless of which control holds focus.)
        if (e.Key == Key.Escape)
        {
            CloseForReal();
            e.Handled = true;
        }
    }

    private static bool TryParseDecimal(string? text, out decimal value)
        => decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value);

    private static string FormatDecimal(decimal value)
        => value.ToString("0.##", CultureInfo.CurrentCulture);

    // ---- element helpers ----------------------------------------------------------------------------

    private static TextBox NumberBox(string text, string automationName)
    {
        var box = new TextBox
        {
            Text = text,
            Width = 72,
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = TextBoxStyle,
        };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    /// <summary>A free-text override field (Claude/Codex plan, Codex reset time) — left-aligned, optional.</summary>
    private static TextBox PlainTextBox(double width, string? text, string automationName)
    {
        var box = new TextBox
        {
            Text = text ?? string.Empty,
            Width = width,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = TextBoxStyle,
        };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    /// <summary>
    /// The Codex weekly-reset day combo: a leading "(not set)" sentinel (null day — unset/optional) followed
    /// by the seven days. Selection defaults to whichever option matches <paramref name="selected"/>, or the
    /// sentinel when <c>null</c>/unmatched.
    /// </summary>
    private static ComboBox DayCombo(DayOfWeek? selected, string automationName)
    {
        var combo = new ComboBox
        {
            ItemsSource = DayOptions,
            Style = ComboBoxStyle,
        };
        combo.SelectedItem = DayOptions.FirstOrDefault(o => o.Day == selected, DayOptions[0]);
        AutomationProperties.SetName(combo, automationName);
        return combo;
    }

    private static CheckBox Check(string content, bool isChecked, string automationName)
    {
        var check = new CheckBox
        {
            Content = content,
            IsChecked = isChecked,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = CheckBoxStyle,
        };
        AutomationProperties.SetName(check, automationName);
        return check;
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
            Margin = new Thickness(0, topMargin, 0, 0),
        };

    private static Button MakeButton(string text, bool primary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12.5,
            Style = primary ? ButtonPrimaryStyle : ButtonSecondaryStyle,
        };
        button.Click += (_, _) => onClick();
        AutomationProperties.SetName(button, text);
        return button;
    }

    // ---- day-of-week combo items ---------------------------------------------------------------------

    /// <summary>One selectable day-combo entry; <see cref="ToString"/> IS its display text (no DataTemplate needed).</summary>
    private readonly record struct DayOption(DayOfWeek? Day, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly IReadOnlyList<DayOption> DayOptions = BuildDayOptions();

    private static List<DayOption> BuildDayOptions()
    {
        var options = new List<DayOption> { new(null, "(not set)") };
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            options.Add(new DayOption(day, day.ToString()));
        }

        return options;
    }

    // ---- styles (VISUAL-IDENTITY.md §5.5 — full REST/HOVER/PRESSED/DISABLED/FOCUS state coverage,
    // every colour from Theme.*, built once and shared across every window instance since the app is
    // dark-only at v1) -------------------------------------------------------------------------------

    private static readonly Style ButtonPrimaryStyle = BuildButtonStyle(primary: true);
    private static readonly Style ButtonSecondaryStyle = BuildButtonStyle(primary: false);
    private static readonly Style TextBoxStyle = BuildTextBoxStyle();
    private static readonly Style CheckBoxStyle = BuildCheckBoxStyle();
    private static readonly Style ComboBoxStyle = BuildComboBoxStyle();

    /// <summary>
    /// A hover-only border literal from VISUAL-IDENTITY.md §5.5's control-state table (TextBox/ComboBox
    /// hover row: "border #4A5866"). Not promoted to a Theme.cs token — it's specific to this one state on
    /// this one window — so it's the exact spec hex rather than an invented colour.
    /// </summary>
    private static readonly Brush InputHoverBorder = Rgb(0x4A, 0x58, 0x66);

    private static Style BuildButtonStyle(bool primary)
    {
        Brush restBg = primary ? Theme.Action : System.Windows.Media.Brushes.Transparent;
        Brush restBorder = primary ? Theme.Action : Theme.Hairline;
        Brush restFg = primary ? Theme.ActionOn : Theme.InkSecondary;

        Brush hoverBg = primary ? Theme.ActionHover : Theme.HoverBg;
        Brush hoverFg = primary ? Theme.ActionOn : Theme.InkPrimary;

        Brush pressedBg = primary ? Theme.ActionPressed : Theme.PressedBg;

        Brush disabledBg = primary ? Theme.DisabledStroke : System.Windows.Media.Brushes.Transparent;

        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, restBg);
        border.SetValue(Border.BorderBrushProperty, restBorder);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(primary ? 0 : 1));
        border.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 7));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
        hover.Setters.Add(new Setter(Button.ForegroundProperty, hoverFg));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, pressedBg, "bd"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, disabledBg, "bd"));
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.DisabledStroke, "bd"));
        disabled.Setters.Add(new Setter(Button.ForegroundProperty, Theme.InkDisabled));
        disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Arrow));
        template.Triggers.Add(disabled);

        var style = new Style(typeof(Button));
        // Button.TemplateProperty / Button.ForegroundProperty resolve to the inherited Control fields;
        // referenced via Button (aliased) to avoid the ambiguous bare `Control` (WinForms vs WPF).
        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        style.Setters.Add(new Setter(Button.ForegroundProperty, restFg));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, BuildFocusRingStyle()));
        return style;
    }

    /// <summary>The 2 DIP action/primary ring, 2 DIP offset, VISUAL-IDENTITY.md §5.5's Button focus cell.</summary>
    private static Style BuildFocusRingStyle()
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.BorderBrushProperty, Theme.Action);
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        // No TargetType (matches the standard FocusVisualStyle technique): WPF instantiates a generic
        // adorner element for the focus ring, not necessarily a Button — so the Style must accept it
        // regardless of its concrete type. Button.TemplateProperty just supplies the (shared, inherited)
        // Control.Template DependencyProperty identity.
        var template = new ControlTemplate { VisualTree = ring };
        var style = new Style();
        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        return style;
    }

    private static Style BuildTextBoxStyle()
    {
        var scrollHost = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        scrollHost.SetValue(FrameworkElement.FocusableProperty, false);
        scrollHost.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        scrollHost.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);

        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, Theme.InsetBg);
        bd.SetValue(Border.BorderBrushProperty, Theme.Hairline);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
        bd.AppendChild(scrollHost);

        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = bd };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, InputHoverBorder, "bd"));
        template.Triggers.Add(hover);

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Action, "bd"));
        template.Triggers.Add(focus);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.DisabledStroke, "bd"));
        disabled.Setters.Add(new Setter(TextBox.ForegroundProperty, Theme.InkDisabled));
        template.Triggers.Add(disabled);

        // Validation (VISUAL-IDENTITY.md §5.5): a Tag == InvalidTag marker (set by OnOk) forces the sev/crit
        // border regardless of hover/focus. Added last so it wins over the other triggers when both match.
        var invalid = new Trigger { Property = FrameworkElement.TagProperty, Value = InvalidTag };
        invalid.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.SevCrit, "bd"));
        template.Triggers.Add(invalid);

        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(TextBox.TemplateProperty, template));
        style.Setters.Add(new Setter(TextBox.ForegroundProperty, Theme.InkPrimary));
        style.Setters.Add(new Setter(TextBox.CaretBrushProperty, Theme.InkPrimary));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
        return style;
    }

    private static Style BuildCheckBoxStyle()
    {
        var box = new FrameworkElementFactory(typeof(Border), "box");
        box.SetValue(FrameworkElement.WidthProperty, 16.0);
        box.SetValue(FrameworkElement.HeightProperty, 16.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        box.SetValue(Border.BorderBrushProperty, Theme.InkMuted);
        box.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var check = new FrameworkElementFactory(typeof(TextBlock), "check");
        check.SetValue(TextBlock.TextProperty, "✓");
        check.SetValue(TextBlock.ForegroundProperty, Theme.ActionOn);
        check.SetValue(TextBlock.FontSizeProperty, 11.0);
        check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(check);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(UIElement.IsHitTestVisibleProperty, false);

        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.AppendChild(box);
        root.AppendChild(content);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = root };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.InkSecondary, "box"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.DisabledStroke, "box"));
        disabled.Setters.Add(new Setter(CheckBox.ForegroundProperty, Theme.InkDisabled));
        template.Triggers.Add(disabled);

        // "Focus / other" (VISUAL-IDENTITY.md §5.5): for CheckBox this is the checked state, not a ring.
        var @checked = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
        @checked.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Action, "box"));
        @checked.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Action, "box"));
        @checked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
        template.Triggers.Add(@checked);

        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(CheckBox.TemplateProperty, template));
        style.Setters.Add(new Setter(CheckBox.ForegroundProperty, Theme.InkPrimary));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
        return style;
    }

    private static Style BuildComboBoxStyle()
    {
        var content = new FrameworkElementFactory(typeof(ContentPresenter), "content");
        content.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem") { RelativeSource = RelativeSource.TemplatedParent });
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 28, 0));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(UIElement.IsHitTestVisibleProperty, false);

        var chevron = new FrameworkElementFactory(typeof(TextBlock));
        chevron.SetValue(TextBlock.TextProperty, "▾");
        chevron.SetValue(TextBlock.ForegroundProperty, Theme.InkSecondary);
        chevron.SetValue(TextBlock.FontSizeProperty, 10.0);
        chevron.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        chevron.SetValue(UIElement.IsHitTestVisibleProperty, false);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton), "toggle");
        toggle.SetValue(ToggleButton.TemplateProperty, BuildInvisibleToggleTemplate());
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
        {
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.TwoWay,
        });
        toggle.SetValue(UIElement.FocusableProperty, false);

        var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        itemsPresenter.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Contained);

        var innerScroll = new FrameworkElementFactory(typeof(ScrollViewer));
        innerScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        innerScroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        innerScroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        innerScroll.AppendChild(itemsPresenter);

        var dropBorder = new FrameworkElementFactory(typeof(Border), "dropBd");
        dropBorder.SetValue(Border.BackgroundProperty, Theme.CardBg);
        dropBorder.SetValue(Border.BorderBrushProperty, Theme.Hairline);
        dropBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        dropBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        dropBorder.SetValue(Border.PaddingProperty, new Thickness(3));
        dropBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);
        dropBorder.AppendChild(innerScroll);

        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.None);
        popup.SetValue(Popup.StaysOpenProperty, false);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent });
        popup.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth") { RelativeSource = RelativeSource.TemplatedParent });
        popup.AppendChild(dropBorder);

        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(content);
        grid.AppendChild(chevron);
        grid.AppendChild(toggle);
        grid.AppendChild(popup);

        var mainBorder = new FrameworkElementFactory(typeof(Border), "bd");
        mainBorder.SetValue(Border.BackgroundProperty, Theme.InsetBg);
        mainBorder.SetValue(Border.BorderBrushProperty, Theme.Hairline);
        mainBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        mainBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        mainBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);
        mainBorder.AppendChild(grid);

        var template = new ControlTemplate(typeof(ComboBox)) { VisualTree = mainBorder };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, InputHoverBorder, "bd"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.DisabledStroke, "bd"));
        disabled.Setters.Add(new Setter(ComboBox.ForegroundProperty, Theme.InkDisabled));
        template.Triggers.Add(disabled);

        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(ComboBox.TemplateProperty, template));
        style.Setters.Add(new Setter(ComboBox.ForegroundProperty, Theme.InkPrimary));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
        style.Setters.Add(new Setter(ComboBox.ItemContainerStyleProperty, BuildComboItemStyle()));
        return style;
    }

    /// <summary>An invisible full-bleed hit-target — visuals come entirely from the ComboBox's own "bd" border.</summary>
    private static ControlTemplate BuildInvisibleToggleTemplate()
    {
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        return new ControlTemplate(typeof(ToggleButton)) { VisualTree = surface };
    }

    /// <summary>Dropdown item row: rest transparent, hover bg/hover, selection bg/selection + ink/primary.</summary>
    private static Style BuildComboItemStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        bd.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        bd.AppendChild(content);

        var template = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = bd };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.HoverBg, "bd"));
        template.Triggers.Add(hover);

        var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Theme.SelectionBg, "bd"));
        selected.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Theme.InkPrimary));
        template.Triggers.Add(selected);

        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(ComboBoxItem.TemplateProperty, template));
        style.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Theme.InkSecondary));
        style.Setters.Add(new Setter(ComboBoxItem.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    /// <summary>
    /// A flat 8 DIP scroll indicator wired directly to <paramref name="target"/> (VISUAL-IDENTITY.md §5.5's
    /// ScrollBar row: transparent track, thumb radius 4, rest/hover/pressed states). Built from plain
    /// composed elements rather than a retemplated <see cref="System.Windows.Controls.Primitives.ScrollBar"/>:
    /// <see cref="Track"/>'s <c>Thumb</c>/<c>IncreaseRepeatButton</c>/<c>DecreaseRepeatButton</c> are plain
    /// CLR properties (no backing <see cref="DependencyProperty"/>), which <see cref="FrameworkElementFactory"/>
    /// — the code-only ControlTemplate mechanism this file uses throughout — cannot set. Drag + mouse wheel
    /// cover every case this window needs; track-click paging is intentionally out of scope.
    /// </summary>
    private static FrameworkElement BuildFlatScrollBar(ScrollViewer target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var track = new Border { Width = 8, Background = System.Windows.Media.Brushes.Transparent };

        var thumb = new Thumb
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Visibility = Visibility.Collapsed,
            Style = BuildScrollThumbStyle(),
        };

        var host = new Grid { Width = 8 };
        host.Children.Add(track);
        host.Children.Add(thumb);

        void Reposition()
        {
            double viewport = target.ViewportHeight;
            double extent = target.ExtentHeight;
            double trackHeight = target.ActualHeight;
            if (viewport <= 0 || trackHeight <= 0 || extent <= viewport)
            {
                thumb.Visibility = Visibility.Collapsed;
                return;
            }

            thumb.Visibility = Visibility.Visible;
            double thumbHeight = Math.Max(24, trackHeight * (viewport / extent));
            double travel = trackHeight - thumbHeight;
            double maxOffset = target.ScrollableHeight;
            double top = maxOffset <= 0 ? 0 : travel * (target.VerticalOffset / maxOffset);

            thumb.Height = thumbHeight;
            thumb.Margin = new Thickness(0, top, 0, 0);
        }

        thumb.DragDelta += (_, e) =>
        {
            double travel = target.ActualHeight - thumb.ActualHeight;
            if (travel <= 0)
            {
                return;
            }

            target.ScrollToVerticalOffset(target.VerticalOffset + (e.VerticalChange / travel * target.ScrollableHeight));
        };

        target.ScrollChanged += (_, _) => Reposition();
        host.SizeChanged += (_, _) => Reposition();

        return host;
    }

    private static Style BuildScrollThumbStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, Theme.Hairline); // rest = #34414D, VISUAL-IDENTITY.md §5.5
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = bd };

        // Hover/pressed literals from the same §5.5 table row as InputHoverBorder — spec-mandated hexes,
        // not promoted to Theme.cs tokens since they're specific to this one scrollbar's two extra states.
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Rgb(0x4A, 0x54, 0x60), "bd"));
        template.Triggers.Add(hover);

        var dragging = new Trigger { Property = Thumb.IsDraggingProperty, Value = true };
        dragging.Setters.Add(new Setter(Border.BackgroundProperty, Rgb(0x59, 0x63, 0x6D), "bd"));
        template.Triggers.Add(dragging);

        var style = new Style(typeof(Thumb));
        style.Setters.Add(new Setter(Thumb.TemplateProperty, template));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 8.0));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        return style;
    }

    private static Brush Rgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---- DWM dark title bar --------------------------------------------------------------------------

    /// <summary>
    /// Minimal DWM P/Invoke for the dark title bar (VISUAL-IDENTITY.md §5.5). Declared locally (not in the
    /// shared NativeMethods.cs) — this window is the only caller. dwmapi.dll is an in-box Windows DLL; no
    /// package is added.
    /// </summary>
    private static class DwmInterop
    {
        private const int DwmwaUseImmersiveDarkModeCurrent = 20; // Win10 20H1+ / Win11
        private const int DwmwaUseImmersiveDarkModeLegacy = 19; // pre-20H1 Win10 builds

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        /// <summary>
        /// Best-effort: ask DWM for a dark title bar, trying the current attribute id first and the legacy
        /// pre-20H1 id if that fails. Any failure (older Windows without either, or no dwmapi) is swallowed —
        /// a light title bar is cosmetic, never fatal.
        /// </summary>
        public static void TryEnableDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int enabled = 1;
                int hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeCurrent, ref enabled, sizeof(int));
                if (hr != 0)
                {
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                System.Diagnostics.Debug.WriteLine($"Dark title bar unavailable: {ex.GetType().FullName}");
            }
        }
    }
}
