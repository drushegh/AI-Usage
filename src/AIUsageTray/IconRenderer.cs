using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// Draws the notification-area icon bitmap — the "Twin-Stop Gauge" live instrument
/// (VISUAL-IDENTITY.md §2.4/§2.5/§4.5). Same silhouette as the static brand mark (two opposing
/// flat-capped arcs with gaps at 12 and 6 o'clock) but drawn as a LIVE gauge: the full twin-stop track is
/// always opaque, and a single severity-coloured fill arc sweeps across it — clockwise, starting at the
/// track's lower-left terminus — for a length proportional to the worst LIVE metric's percentage. Colour
/// here answers "how urgent, right now" (severity), never "whose data" (provider identity is amber/teal
/// and belongs to the brand mark + popup only). The caller (<see cref="TrayIconController"/>) computes the
/// <see cref="TrayIconState"/> from the current <c>UsageView</c> and converts the returned
/// <see cref="Bitmap"/> to an <c>HICON</c>, owning its lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mixed-state truth table (§4.5, R1–R6 — normative).</b> R1: the arc's colour AND length come from the
/// single worst LIVE metric only (<see cref="TrayIconState.Compute"/> — never mixed sources). R2: any
/// provider not fully LIVE shows the <c>?</c> badge. R3: no LIVE data anywhere ⇒ no arc at all — track
/// only, never a grey "last-known" arc, never green by default. R4: at 16 physical px the badge REPLACES
/// the centre glyph; at ≥20 px they may coexist. R5: DATED magnitude/severity never reaches this renderer
/// — it lives only in the tooltip/popup's DATED grammar. R6: pre-first-fetch renders as track + badge only.
/// </para>
/// <para>
/// <b>Physical pixels.</b> <paramref name="size"/> is the DPI-scaled physical size the caller resolved for
/// the taskbar's monitor (16 @100%, 20 @125%, 24 @150%, 32 @200%…); every measure here is a continuous
/// ratio of that size (§2.5) rather than a hand-pixeled master — the live instrument does not need the
/// per-size static-icon degradation ladder in §2.3, which governs the separate committed .ico asset only.
/// </para>
/// <para>
/// <b>High Contrast (§2.5).</b> When <paramref name="highContrast"/> is set, every token collapses to
/// <see cref="SystemColors.Window"/> (surfaces) / <see cref="SystemColors.WindowText"/> (everything else) —
/// state is then carried entirely by the track/fill shapes and the badge, never by hue.
/// </para>
/// </remarks>
public static class IconRenderer
{
    /// <summary>Physical-pixel floor below which the icon is never drawn (§2.5's 16 px baseline).</summary>
    private const int MinSize = 16;

    /// <summary>Physical-pixel threshold at/above which the centre glyph may coexist with the badge (R4).</summary>
    private const int CentreGlyphMinSize = 20;

    /// <summary>
    /// Render one tray frame. Returns a freshly allocated <see cref="Bitmap"/> (the caller disposes it
    /// after taking an HICON). Disposes its own bitmap on any draw failure.
    /// </summary>
    /// <param name="state">The derived truth-table state (<see cref="TrayIconState.Compute"/>).</param>
    /// <param name="size">Icon side in physical pixels (the taskbar monitor's DPI-scaled small-icon size); clamped to a 16 px floor.</param>
    /// <param name="highContrast">Whether Windows High Contrast is active (<c>SystemInformation.HighContrast</c>).</param>
    public static Bitmap Render(TrayIconState state, int size, bool highContrast)
    {
        if (size < MinSize)
        {
            size = MinSize;
        }

        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var committed = false;
        try
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                var layout = Layout.For(size);
                DrawTile(g, layout, highContrast);
                DrawTrack(g, layout, highContrast);

                if (state.AnyLive)
                {
                    DrawFillArc(g, layout, state.Severity, state.Percent, highContrast);

                    // Centre glyph: S >= 20 only, and only for a real warn/crit reading (R1's "if
                    // applicable") — a healthy LIVE arc (sev/ok) draws no centre shape.
                    if (size >= CentreGlyphMinSize && state.Severity != Severity.Normal)
                    {
                        DrawCentreGlyph(g, layout, state.Severity, highContrast);
                    }
                }

                // R4: at 16 px the badge REPLACES the centre glyph rather than joining it. That is already
                // true here without a special case — the centre glyph never draws below CentreGlyphMinSize,
                // so the badge is simply the only thing that can occupy that space at S = 16.
                if (state.Badge)
                {
                    DrawBadge(g, layout, highContrast);
                }
            }

            committed = true;
            return bitmap;
        }
        finally
        {
            if (!committed)
            {
                bitmap.Dispose();
            }
        }
    }

    private static void DrawTile(Graphics g, Layout layout, bool highContrast)
    {
        Color fill = highContrast ? SystemColors.Window : ToGdi(Theme.TrayTile);
        Color keyline = highContrast ? SystemColors.WindowText : ToGdi(Theme.TrayKeyline);

        using var path = RoundedRect(layout.TileRect, layout.TileRadius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);

        // 1-physical-pixel keyline (§2.5) — a hairline regardless of S, never a scaled stroke.
        using var pen = new Pen(keyline, 1f);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// The full twin-stop silhouette (both flat-capped brackets), always drawn in full as the opaque
    /// track — regardless of live data (§2.5: "never a transparent alpha grey"; §6.4 theme lifecycle keeps
    /// this in sync with taskbar recreation/DPI/theme changes at the caller).
    /// </summary>
    private static void DrawTrack(Graphics g, Layout layout, bool highContrast)
    {
        Color track = highContrast ? SystemColors.WindowText : ToGdi(Theme.TrayTrack);
        using var pen = new Pen(track, layout.ArcStroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };

        var bounds = layout.ArcBounds;
        float half = layout.ArcHalfSpanDeg;
        g.DrawArc(pen, bounds, RightArcCentreDeg - half, 2f * half);
        g.DrawArc(pen, bounds, LeftArcCentreDeg - half, 2f * half);
    }

    /// <summary>
    /// The single severity-coloured fill, swept clockwise along the track path starting at its
    /// lower-left terminus (the left bracket's low end, near 6 o'clock) — first consuming the left
    /// bracket, then (past the 12 o'clock gap, which the fill never occupies) the right bracket, for a
    /// length proportional to <paramref name="percent"/>, quantised to whole device pixels (§2.5).
    /// </summary>
    private static void DrawFillArc(Graphics g, Layout layout, Severity severity, decimal percent, bool highContrast)
    {
        float half = layout.ArcHalfSpanDeg;
        float armDeg = 2f * half; // one bracket's own sweep
        float totalDeg = 2f * armDeg; // both brackets, gaps excluded — the gauge's full trackable length

        float fraction = (float)Math.Clamp(percent / 100m, 0m, 1m);
        float circumferencePx = (totalDeg / 360f) * 2f * MathF.PI * layout.ArcRadius;
        float filledPx = MathF.Round(fraction * circumferencePx);
        float filledDeg = circumferencePx <= 0f ? 0f : (filledPx / circumferencePx) * totalDeg;

        if (filledDeg <= 0f)
        {
            return; // 0% honestly shows track only — no artificial minimum arc (§2.5, "Settled").
        }

        Color color = SeverityColor(severity, highContrast);
        using var pen = new Pen(color, layout.ArcStroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        var bounds = layout.ArcBounds;

        float leftStart = LeftArcCentreDeg - half; // the lower-left terminus
        float leftSweep = Math.Min(filledDeg, armDeg);
        g.DrawArc(pen, bounds, leftStart, leftSweep);

        float remainder = filledDeg - armDeg;
        if (remainder > 0f)
        {
            float rightStart = RightArcCentreDeg - half; // the right bracket's own lower terminus
            float rightSweep = Math.Min(remainder, armDeg);
            g.DrawArc(pen, bounds, rightStart, rightSweep);
        }
    }

    /// <summary>
    /// The warn triangle / critical "!" (S ≥ 20 only — R4), confined inside the arc's inner edge so it
    /// never collides with the track/fill ring. Never colour-only: the triangle and the "!" are distinct
    /// silhouettes even in greyscale or under a colour-vision simulation.
    /// </summary>
    private static void DrawCentreGlyph(Graphics g, Layout layout, Severity severity, bool highContrast)
    {
        Color color = SeverityColor(severity, highContrast);
        float innerRadius = layout.ArcRadius - (layout.ArcStroke / 2f);
        float glyphSide = innerRadius * 1.5f; // comfortably inside the ring, allowing for the diagonal
        float x = layout.CentreX - (glyphSide / 2f);
        float y = layout.CentreY - (glyphSide / 2f);

        using var brush = new SolidBrush(color);

        if (severity == Severity.Critical)
        {
            float barWidth = glyphSide * 0.26f;
            float barHeight = glyphSide * 0.58f;
            float barX = layout.CentreX - (barWidth / 2f);
            g.FillRectangle(brush, barX, y, barWidth, barHeight);

            float dotSize = barWidth;
            float dotY = y + glyphSide - dotSize;
            g.FillEllipse(brush, barX, dotY, dotSize, dotSize);
        }
        else
        {
            var triangle = new[]
            {
                new PointF(layout.CentreX, y),
                new PointF(x, y + glyphSide),
                new PointF(x + glyphSide, y + glyphSide),
            };
            g.FillPolygon(brush, triangle);
        }
    }

    /// <summary>
    /// The "?" badge (§2.5): present whenever coverage is incomplete (R2/R3). Sits inside the tile's
    /// upper-right corner — a self-contrasting disc + dark glyph so it reads regardless of what the arc
    /// underneath is doing.
    /// </summary>
    private static void DrawBadge(Graphics g, Layout layout, bool highContrast)
    {
        float diameter = layout.BadgeDiameter;
        float pad = layout.Size * 0.02f;
        var rect = new RectangleF(
            layout.Size - layout.TileInset - diameter - pad,
            layout.TileInset + pad,
            diameter,
            diameter);

        Color disc = highContrast ? SystemColors.Window : ToGdi(Theme.TrayBadgeDisc);
        Color ink = highContrast ? SystemColors.WindowText : ToGdi(Theme.TrayBadgeInk);

        using (var discBrush = new SolidBrush(disc))
        using (var ring = new Pen(ink, Math.Max(1f, layout.Size * 0.05f)))
        {
            g.FillEllipse(discBrush, rect);
            g.DrawEllipse(ring, rect);
        }

        using var font = new Font("Segoe UI", diameter * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var glyphBrush = new SolidBrush(ink);
        using var format = CenteredFormat();
        g.DrawString("?", font, glyphBrush, rect, format);
    }

    private static Color SeverityColor(Severity severity, bool highContrast)
    {
        if (highContrast)
        {
            return SystemColors.WindowText;
        }

        var brush = (System.Windows.Media.SolidColorBrush)Theme.SeverityBrush(severity);
        return ToGdi(brush.Color);
    }

    private static Color ToGdi(System.Windows.Media.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        if (d <= 0f || d >= rect.Width || d >= rect.Height)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static StringFormat CenteredFormat() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };

    // GDI+ arc angles: 0deg = 3 o'clock (east), positive sweep = clockwise (GDI+'s y-down screen
    // coordinates make increasing angle read as visually clockwise, matching VISUAL-IDENTITY.md §2.1's
    // CCW-from-east master angles by symmetry about each bracket's own axis).
    private const float RightArcCentreDeg = 0f;   // 3 o'clock
    private const float LeftArcCentreDeg = 180f;  // 9 o'clock

    private readonly record struct Layout(
        float Size,
        float TileInset,
        float TileRadius,
        float CentreX,
        float CentreY,
        float ArcRadius,
        float ArcStroke,
        float ArcHalfSpanDeg,
        float BadgeDiameter)
    {
        /// <summary>
        /// Continuous ratios anchored to the 256 px master (VISUAL-IDENTITY.md §2.1/§2.5): tile inset
        /// 16/256, corner radius 0.19×S, arc radius 70/256, arc stroke 28/256. The live tray instrument
        /// is rendered, not hand-pixeled, so a formula is correct here — §2.3's per-size table governs
        /// only the separate committed .ico asset.
        /// </summary>
        public static Layout For(int size)
        {
            float s = size;
            float inset = s * (16f / 256f);
            float radius = s * 0.19f;
            float arcRadius = s * (70f / 256f);
            float arcStroke = Math.Max(1.4f, s * (28f / 256f));

            // Gap centred at 12 & 6 o'clock, widened at small S so it survives anti-aliasing instead of
            // smearing shut (the live-render analogue of §2.3's degradation-ladder intent). Each bracket's
            // half-span about its own axis is 90° minus half the gap.
            float gapDeg = s >= 32f ? 28f : 28f + ((32f - s) * 0.9f);
            float halfSpan = 90f - (gapDeg / 2f);

            float badgeDiameter = s * 0.38f;

            return new Layout(s, inset, radius, s / 2f, s / 2f, arcRadius, arcStroke, halfSpan, badgeDiameter);
        }

        public RectangleF TileRect => new(TileInset, TileInset, Size - (2f * TileInset), Size - (2f * TileInset));

        public RectangleF ArcBounds => new(CentreX - ArcRadius, CentreY - ArcRadius, ArcRadius * 2f, ArcRadius * 2f);
    }
}
