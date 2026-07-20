using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// This project enables UseWPF *and* UseWindowsForms, and ImplicitUsings pulls `System.Windows.Forms` +
// `System.Drawing` into global scope, making several simple type names ambiguous (WinForms/GDI vs WPF).
// Alias each one this window uses to its WPF type — matching UsagePopup's approach.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AIUsageTray;

/// <summary>
/// The values the user accepted in the <see cref="SettingsWindow"/> (raise on OK; never on Cancel).
/// The App applies these LIVE and persists them (DESIGN.md §7; tasks T41/T38).
/// </summary>
/// <param name="WarnPercent">The chosen warning threshold (already validated 1 ≤ warn &lt; crit ≤ 100).</param>
/// <param name="CritPercent">The chosen critical threshold.</param>
/// <param name="CodexTtlMinutes">The chosen Codex "current" TTL, in minutes (validated 1..1440).</param>
/// <param name="ClaudeEnabled">Whether the Claude usage provider should be enabled.</param>
/// <param name="StartWithWindows">Whether AI-Usage should start at sign-in (the App writes the HKCU Run value).</param>
public sealed record SettingsResult(
    decimal WarnPercent,
    decimal CritPercent,
    int CodexTtlMinutes,
    bool ClaudeEnabled,
    bool StartWithWindows);

/// <summary>
/// The owner-tunable Settings window (DESIGN.md §7; tasks T41/T38). A code-only WPF <see cref="Window"/>
/// (no XAML/BAML — matching <see cref="UsagePopup"/>) that edits the display thresholds, the Codex
/// freshness TTL, the Claude opt-in, and the "start at sign-in" registration. It is theme-aware
/// (<see cref="SystemTheme.IsLightAppTheme"/>), fully keyboard-navigable (Tab order, Enter = OK,
/// Esc = Cancel), and every input carries an <see cref="AutomationProperties"/> name for Narrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apply/discard.</b> OK validates via <see cref="SettingsValidation.Validate"/>; on success it raises
/// the <see cref="SettingsResult"/> callback (the App applies + persists) and closes. On a validation
/// failure it shows an inline message and keeps the window open. Cancel (and Esc) close without raising —
/// nothing is applied.
/// </para>
/// <para>
/// <b>Threading.</b> Built and shown on the one WPF dispatcher thread. It does no I/O and never blocks —
/// the registry read for the initial checkbox state happens in the App before construction and is passed
/// in as a plain <c>bool</c>, so this window has zero registry coupling.
/// </para>
/// </remarks>
public sealed class SettingsWindow : Window
{
    private const double ContentWidth = 400;

    private readonly Action<SettingsResult> _onApply;
    private readonly TextBox _warnBox;
    private readonly TextBox _critBox;
    private readonly TextBox _ttlBox;
    private readonly CheckBox _claudeCheck;
    private readonly CheckBox _startupCheck;
    private readonly TextBlock _errorText;
    private readonly Theme _theme;
    private bool _closedForReal;

    /// <param name="config">The current persisted config — seeds the threshold / TTL / Claude inputs.</param>
    /// <param name="startWithWindows">The current HKCU Run autostart state (read by the App) — seeds that checkbox.</param>
    /// <param name="onApply">Invoked with the accepted values on OK (never on Cancel).</param>
    public SettingsWindow(AppConfig config, bool startWithWindows, Action<SettingsResult> onApply)
    {
        ArgumentNullException.ThrowIfNull(config);
        _onApply = onApply ?? throw new ArgumentNullException(nameof(onApply));
        _theme = Theme.ForTheme(SystemTheme.IsLightAppTheme());

        Title = "AI-Usage Settings";
        WindowStyle = WindowStyle.SingleBorderWindow; // title bar + close button — a real dialog, movable
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.Height;
        Width = ContentWidth + 2; // + the 1px border on each side
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = _theme.WindowBg;
        AutomationProperties.SetName(this, "AI-Usage settings");

        _warnBox = NumberBox(FormatDecimal(config.WarnPercent), "Warning threshold percent");
        _critBox = NumberBox(FormatDecimal(config.CritPercent), "Critical threshold percent");
        _ttlBox = NumberBox(config.CodexTtlMinutes.ToString(CultureInfo.CurrentCulture), "Codex current window in minutes");

        _claudeCheck = Check("Enable Claude usage", config.ClaudeEnabled, "Enable Claude usage");
        _startupCheck = Check("Start AI-Usage when I sign in", startWithWindows, "Start AI-Usage when I sign in");

        _errorText = new TextBlock
        {
            Foreground = _theme.Critical,
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
        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

        stack.Children.Add(Text("Settings", _theme.Text, 17, FontWeights.Bold));
        stack.Children.Add(Text("Tune thresholds, freshness, and startup", _theme.SubtleText, 12, FontWeights.Normal, topMargin: 2));

        stack.Children.Add(SectionHeader("Alert thresholds"));
        stack.Children.Add(FieldRow("Warning at", _warnBox, "%"));
        stack.Children.Add(FieldRow("Critical at", _critBox, "%"));
        stack.Children.Add(Hint("Warning must be below critical. Both compared against the exact, unrounded usage."));

        stack.Children.Add(SectionHeader("Codex freshness"));
        stack.Children.Add(FieldRow("Current window", _ttlBox, "min"));
        stack.Children.Add(Hint("How long a Codex reading counts as live — longer than your typical session gap."));

        stack.Children.Add(SectionHeader("Providers"));
        stack.Children.Add(_claudeCheck);
        stack.Children.Add(Hint("Uses an undocumented endpoint (ToS-grey); reads only your own account."));

        stack.Children.Add(SectionHeader("Startup"));
        stack.Children.Add(_startupCheck);

        stack.Children.Add(_errorText);
        stack.Children.Add(BuildButtonRow());

        return new Border
        {
            BorderBrush = _theme.Border,
            BorderThickness = new Thickness(1),
            Background = _theme.WindowBg,
            Child = stack,
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
            Margin = new Thickness(0, 20, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        return row;
    }

    private FrameworkElement SectionHeader(string title)
    {
        var text = Text(title.ToUpperInvariant(), _theme.SubtleText, 11, FontWeights.SemiBold, topMargin: 18);
        return text;
    }

    /// <summary>A label / input / unit row: label takes the remaining width, the input + unit hug the right.</summary>
    private FrameworkElement FieldRow(string label, TextBox input, string unit)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = Text(label, _theme.Text, 13, FontWeights.Normal);
        labelText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(labelText, 0);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(input);
        var unitText = Text(unit, _theme.SubtleText, 12.5, FontWeights.Normal);
        unitText.Margin = new Thickness(6, 0, 0, 0);
        unitText.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(unitText);
        Grid.SetColumn(right, 1);

        grid.Children.Add(labelText);
        grid.Children.Add(right);
        return grid;
    }

    private FrameworkElement Hint(string text)
        => Text(text, _theme.SubtleText, 11.5, FontWeights.Normal, wrap: true, topMargin: 4);

    // ---- apply --------------------------------------------------------------------------------------

    private void OnOk()
    {
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

        _onApply(new SettingsResult(warn, crit, ttl, _claudeCheck.IsChecked == true, _startupCheck.IsChecked == true));
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

    private TextBox NumberBox(string text, string automationName)
    {
        var box = new TextBox
        {
            Text = text,
            Width = 72,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = _theme.InputBg,
            Foreground = _theme.Text,
            BorderBrush = _theme.Border,
            BorderThickness = new Thickness(1),
            CaretBrush = _theme.Text,
        };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    private CheckBox Check(string content, bool isChecked, string automationName)
    {
        var check = new CheckBox
        {
            Content = content,
            IsChecked = isChecked,
            Foreground = _theme.Text,
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
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

    private Button MakeButton(string text, bool primary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12.5,
            Style = BuildButtonStyle(_theme, primary),
        };
        button.Click += (_, _) => onClick();
        AutomationProperties.SetName(button, text);
        return button;
    }

    // ---- flat button style (built per button; both share the theme) ---------------------------------

    private static Style BuildButtonStyle(Theme theme, bool primary)
    {
        Brush baseBg = primary ? theme.AccentBg : theme.ButtonBg;
        Brush hoverBg = primary ? theme.AccentHover : theme.ButtonHover;
        Brush pressBg = primary ? theme.AccentPressed : theme.ButtonPressed;
        Brush foreground = primary ? theme.AccentFg : theme.Text;

        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, baseBg);
        border.SetValue(Border.BorderBrushProperty, theme.Border);
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
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, pressBg, "bd"));
        template.Triggers.Add(pressed);

        var style = new Style(typeof(Button));
        // Referenced via Button (aliased) so the inherited Control fields don't reintroduce the ambiguous
        // bare `Control` (WinForms vs WPF), matching UsagePopup.
        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        style.Setters.Add(new Setter(Button.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        return style;
    }

    // ---- theme --------------------------------------------------------------------------------------

    /// <summary>The window's colour set (light/dark), keyed off <c>AppsUseLightTheme</c> like the popup.</summary>
    private sealed class Theme
    {
        public readonly Brush WindowBg;
        public readonly Brush InputBg;
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
        public readonly Brush Critical;

        private Theme(bool light)
        {
            if (light)
            {
                WindowBg = B(0xFF, 0xFF, 0xFF);
                InputBg = B(0xFF, 0xFF, 0xFF);
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
                Critical = B(0xCF, 0x22, 0x2E);
            }
            else
            {
                WindowBg = B(0x1E, 0x1F, 0x22);
                InputBg = B(0x2A, 0x2C, 0x30);
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
                Critical = B(0xF0, 0x44, 0x38);
            }
        }

        public static Theme ForTheme(bool light) => new(light);

        private static Brush B(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
