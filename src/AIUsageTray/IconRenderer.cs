using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// Draws the notification-area icon bitmap from the render model's icon triple —
/// <see cref="UsageView.OverallSeverity"/> + <see cref="UsageView.Unknown"/> +
/// <see cref="UsageView.AllUnknown"/> (DESIGN.md §7; tasks T10/T11). The caller converts the
/// returned <see cref="Bitmap"/> to an <c>HICON</c> and owns its lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never colour-only (§7).</b> Severity varies SHAPE as well as colour so the state survives
/// greyscale / high-contrast / colour-vision differences: Normal → a calm disc, Warning → the
/// universal caution triangle, Critical → a distinct square. Warning vs Critical is therefore
/// legible without relying on amber-vs-red.
/// </para>
/// <para>
/// <b>Unknown-state propagation (§7).</b> When something expected is not LIVE but a real severity
/// signal still exists, the severity shape is drawn PLUS a distinct corner "?" badge — never a plain
/// safe icon. When nothing is known at all (<see cref="UsageView.AllUnknown"/>) the whole icon becomes
/// a neutral grey "?" glyph that reads as "cannot tell", never as "safe".
/// </para>
/// <para>
/// <b>DPI.</b> The caller passes the shell's small-icon size, so the icon is drawn crisp at that size
/// rather than a 16 px bitmap upscaled by the shell. The final §9 form (combined vs two-bar) is decided
/// by the deferred E4 16 px render test; this renderer implements the synthesizer's lean — the combined
/// worst-of icon with the unknown badge.
/// </para>
/// </remarks>
public static class IconRenderer
{
    /// <summary>
    /// Render the icon for one render-model state. Returns a freshly allocated <see cref="Bitmap"/>
    /// (the caller disposes it after taking an HICON). Disposes its own bitmap on any draw failure.
    /// </summary>
    /// <param name="severity">The worst LIVE/monotone-floor severity (drives shape + colour).</param>
    /// <param name="unknown">Whether an expected region is not LIVE (adds the corner badge).</param>
    /// <param name="allUnknown">Whether nothing contributes a real signal (the neutral "?" state).</param>
    /// <param name="size">Icon side in pixels (the shell small-icon size); clamped to a 16 px floor.</param>
    /// <param name="lightTaskbar">Whether the taskbar is light — flips the contrast-outline colour.</param>
    public static Bitmap Render(Severity severity, bool unknown, bool allUnknown, int size, bool lightTaskbar)
    {
        if (size < 16)
        {
            size = 16;
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

                // A thin outline in the taskbar's opposite luminance keeps every shape legible on both
                // a light and a dark taskbar (theme-aware — DESIGN.md §7 nice-to-have).
                Color ink = lightTaskbar
                    ? Color.FromArgb(0x20, 0x24, 0x28)
                    : Color.FromArgb(0xF2, 0xF4, 0xF6);

                if (allUnknown)
                {
                    DrawAllUnknown(g, size, ink);
                }
                else
                {
                    DrawSeverity(g, severity, size, ink);
                    if (unknown)
                    {
                        DrawUnknownBadge(g, size);
                    }
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

    private static void DrawSeverity(Graphics g, Severity severity, int size, Color ink)
    {
        Color fill = severity switch
        {
            Severity.Critical => Color.FromArgb(0xD9, 0x33, 0x2B), // red
            Severity.Warning => Color.FromArgb(0xE8, 0x9B, 0x1C),  // amber
            _ => Color.FromArgb(0x2E, 0xA0, 0x43),                 // green (Normal)
        };

        float margin = size * 0.14f;
        float x = margin;
        float y = margin;
        float side = size - (2 * margin);
        float penWidth = Math.Max(1f, size * 0.06f);

        using var brush = new SolidBrush(fill);
        using var pen = new Pen(ink, penWidth) { LineJoin = LineJoin.Round };

        switch (severity)
        {
            case Severity.Warning:
                // Up-triangle — the real-world caution silhouette; distinct in greyscale from disc/square.
                var triangle = new[]
                {
                    new PointF(x + (side / 2f), y),
                    new PointF(x, y + side),
                    new PointF(x + side, y + side),
                };
                g.FillPolygon(brush, triangle);
                g.DrawPolygon(pen, triangle);
                break;

            case Severity.Critical:
                // Square — an unmistakably different silhouette from the triangle, colour aside.
                g.FillRectangle(brush, x, y, side, side);
                g.DrawRectangle(pen, x, y, side, side);
                break;

            default:
                // Disc — the calm safe state.
                g.FillEllipse(brush, x, y, side, side);
                g.DrawEllipse(pen, x, y, side, side);
                break;
        }
    }

    /// <summary>
    /// A corner "?" badge overlaid on the severity shape: a self-contrasting white disc + dark ring +
    /// dark "?" so it reads on any taskbar. Signals "known figure, but something expected is unknown".
    /// </summary>
    private static void DrawUnknownBadge(Graphics g, int size)
    {
        float diameter = size * 0.62f;
        float bx = size - diameter;
        float by = size - diameter;
        var rect = new RectangleF(bx, by, diameter, diameter);
        Color mark = Color.FromArgb(0x22, 0x26, 0x2A);

        using (var bg = new SolidBrush(Color.White))
        using (var ring = new Pen(mark, Math.Max(1f, size * 0.05f)))
        {
            g.FillEllipse(bg, rect);
            g.DrawEllipse(ring, rect);
        }

        using var font = new Font("Segoe UI", diameter * 0.66f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var glyph = new SolidBrush(mark);
        using var format = CenteredFormat();
        g.DrawString("?", font, glyph, rect, format);
    }

    /// <summary>
    /// The all-unknown "?" glyph (DESIGN.md §7): a neutral grey disc — deliberately NOT the green safe
    /// disc — filled with a large white "?" so it reads "cannot tell", never "safe".
    /// </summary>
    private static void DrawAllUnknown(Graphics g, int size, Color ink)
    {
        float margin = size * 0.10f;
        float x = margin;
        float y = margin;
        float side = size - (2 * margin);
        var rect = new RectangleF(x, y, side, side);

        using (var grey = new SolidBrush(Color.FromArgb(0x6C, 0x74, 0x7C)))
        using (var pen = new Pen(ink, Math.Max(1f, size * 0.06f)))
        {
            g.FillEllipse(grey, rect);
            g.DrawEllipse(pen, rect);
        }

        using var font = new Font("Segoe UI", side * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var glyph = new SolidBrush(Color.White);
        using var format = CenteredFormat();
        g.DrawString("?", font, glyph, rect, format);
    }

    private static StringFormat CenteredFormat() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };
}
