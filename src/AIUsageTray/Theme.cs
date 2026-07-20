using System.Windows.Media;
// UseWindowsForms=true pulls System.Drawing into global scope, which also declares a `Brush` type —
// alias to disambiguate (matches the pattern UsagePopup.cs already uses for the same conflict).
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace AIUsageTray;

/// <summary>
/// The single source of the app's visual-identity colour tokens (VISUAL-IDENTITY.md §3 / DEC-032).
/// Dark theme is the shipped identity; every brush is frozen for cross-thread reuse. The app must
/// read a hex ONLY through here — never hard-code one in a window or the icon renderer.
///
/// <para><b>Organising invariant.</b> Provider hues (amber/teal) answer only "whose data?"; severity
/// hues (green/yellow/red) answer only "act now?"; interaction blue answers only "clickable?". Accuracy
/// (LIVE/DATED/n-a) is NOT a hue — it is carried by saturation + shape + words. So a LIVE bar is the
/// provider colour, a DATED bar is the pre-composited <c>*Dated</c> variant (+ hatch), and n-a has no
/// fill at all.</para>
/// </summary>
internal static class Theme
{
    // ---- surfaces ----
    public static readonly Brush WindowBg = B(0x11, 0x17, 0x1C);   // popup + settings canvas
    public static readonly Brush CardBg = B(0x18, 0x20, 0x27);     // provider cards, combo popups
    public static readonly Brush HoverBg = B(0x20, 0x2A, 0x33);
    public static readonly Brush PressedBg = B(0x1D, 0x26, 0x2E);
    public static readonly Brush InsetBg = B(0x2B, 0x36, 0x40);    // bar tracks, input wells
    public static readonly Brush SelectionBg = B(0x30, 0x42, 0x5D);
    public static readonly Brush Hairline = B(0x34, 0x41, 0x4D);   // borders, separators
    public static readonly Brush DisabledStroke = B(0x2A, 0x34, 0x3E);

    // ---- ink ----
    public static readonly Brush InkPrimary = B(0xF3, 0xF6, 0xF8);   // LIVE numerals only, headings
    public static readonly Brush InkSecondary = B(0xB7, 0xC0, 0xC8); // labels, DATED numerals, chip text
    public static readonly Brush InkMuted = B(0x88, 0x94, 0x9F);     // captions, freshness line, chip borders
    public static readonly Brush InkDisabled = B(0x5A, 0x64, 0x6E);

    // ---- provider (identity) ----
    public static readonly Brush Claude = B(0xEA, 0xA9, 0x3E);       // amber — Claude LIVE fills, underline
    public static readonly Brush Codex = B(0x43, 0xC9, 0xB8);        // teal  — Codex LIVE fills, underline
    public static readonly Brush ClaudeDated = B(0xA3, 0x83, 0x4E);  // pre-composited DATED amber
    public static readonly Brush CodexDated = B(0x54, 0x91, 0x86);   // pre-composited DATED teal

    // ---- severity (accent only; always paired with a shape + word) ----
    public static readonly Brush SevOk = B(0x5B, 0xCB, 0x7A);        // tray arc only
    public static readonly Brush SevWarn = B(0xFF, 0xD1, 0x66);      // warn glyph/word/numeral/tick + tray
    public static readonly Brush SevCrit = B(0xFF, 0x66, 0x75);      // crit glyph/word/numeral/tick + tray

    // ---- accuracy ----
    public static readonly Brush Hatch = B(0x10, 0x16, 0x1C);       // 45deg hatch over *Dated fills

    // ---- interaction (never data state) ----
    public static readonly Brush Action = B(0x78, 0xA9, 0xFF);
    public static readonly Brush ActionOn = B(0x0E, 0x12, 0x16);
    public static readonly Brush ActionHover = B(0x8F, 0xB8, 0xFF);
    public static readonly Brush ActionPressed = B(0x6C, 0x9B, 0xF0);

    // ---- tray instrument (physical-pixel; all opaque) ----
    public static readonly Color TrayTile = Color.FromRgb(0x18, 0x20, 0x27);
    public static readonly Color TrayKeyline = Color.FromRgb(0x6B, 0x76, 0x80);
    public static readonly Color TrayTrack = Color.FromRgb(0x6B, 0x76, 0x80);
    public static readonly Color TrayBadgeDisc = Color.FromRgb(0xC9, 0xD2, 0xDA);
    public static readonly Color TrayBadgeInk = Color.FromRgb(0x0E, 0x12, 0x16);

    /// <summary>The provider's LIVE identity fill (amber = Claude, teal = Codex). Unknown ids fall to teal.</summary>
    public static Brush ProviderBrush(string providerId)
        => string.Equals(providerId, "claude", StringComparison.OrdinalIgnoreCase) ? Claude : Codex;

    /// <summary>The provider's pre-composited DATED fill (never a raw alpha — VISUAL-IDENTITY.md §3).</summary>
    public static Brush ProviderDatedBrush(string providerId)
        => string.Equals(providerId, "claude", StringComparison.OrdinalIgnoreCase) ? ClaudeDated : CodexDated;

    /// <summary>Severity accent colour (used only alongside a glyph + word). Normal has no popup colour.</summary>
    public static Brush SeverityBrush(AIUsage.Core.Severity severity) => severity switch
    {
        AIUsage.Core.Severity.Critical => SevCrit,
        AIUsage.Core.Severity.Warning => SevWarn,
        _ => SevOk,
    };

    private static SolidColorBrush B(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
