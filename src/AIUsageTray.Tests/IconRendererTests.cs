using System.Drawing;
using AIUsage.Core;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Coverage for the Twin-Stop Gauge tray instrument renderer (VISUAL-IDENTITY.md §2.5/§4.5). Beyond the
/// original smoke assertions (every state renders at the requested size without throwing), these tests
/// scan the rendered pixels for the documented tray colour tokens (§3) so the truth table's "a severity
/// colour must/must never appear" rules (R2/R3/R6) are exercised against real output — not just against
/// <see cref="TrayIconState"/>, which only asserts the INPUT the renderer receives (covered separately in
/// <c>TrayIconStateTests</c>). The expected hexes are pinned directly from VISUAL-IDENTITY.md §3, not read
/// back from <c>Theme</c> (internal to the tray assembly), so a broken token wire-up would also fail here.
/// </summary>
public sealed class IconRendererTests
{
    // VISUAL-IDENTITY.md §3 "Tray" + "Severity" token tables — pinned independently of Theme.cs.
    private static readonly Color TrayTile = Color.FromArgb(0x18, 0x20, 0x27);
    private static readonly Color TrayTrack = Color.FromArgb(0x6B, 0x76, 0x80);
    private static readonly Color TrayBadgeDisc = Color.FromArgb(0xC9, 0xD2, 0xDA);
    private static readonly Color SevOk = Color.FromArgb(0x5B, 0xCB, 0x7A);
    private static readonly Color SevWarn = Color.FromArgb(0xFF, 0xD1, 0x66);
    private static readonly Color SevCrit = Color.FromArgb(0xFF, 0x66, 0x75);

    public static IEnumerable<object[]> States() =>
    [
        [new TrayIconState(AnyLive: true, Severity.Normal, 42m, Badge: false)],   // R1: fully live, healthy
        [new TrayIconState(AnyLive: true, Severity.Warning, 82m, Badge: false)],  // R1: fully live, warn
        [new TrayIconState(AnyLive: true, Severity.Critical, 96m, Badge: true)],  // R2: live + badge
        [new TrayIconState(AnyLive: false, Severity.Normal, 0m, Badge: true)],    // R4-R6: track + badge only
        [TrayIconState.NoData],
    ];

    [Theory]
    [MemberData(nameof(States))]
    public void Render_ProducesRequestedSize(TrayIconState state)
    {
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var bitmap = IconRenderer.Render(state, size, highContrast: false);

            Assert.NotNull(bitmap);
            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
        }
    }

    [Fact]
    public void Render_ClampsBelowSixteenToTheFloor()
    {
        using var bitmap = IconRenderer.Render(TrayIconState.NoData, size: 4, highContrast: true);

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
    }

    [Fact]
    public void Render_OutsideTheTileStaysTransparent()
    {
        using var bitmap = IconRenderer.Render(TrayIconState.NoData, size: 32, highContrast: false);

        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(31, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, 31).A);
        Assert.Equal(0, bitmap.GetPixel(31, 31).A);
    }

    [Fact]
    public void Render_TileIsDrawn()
    {
        using var bitmap = IconRenderer.Render(TrayIconState.NoData, size: 32, highContrast: false);

        Assert.True(ContainsColor(bitmap, TrayTile), "the dark tile field must be painted (§2.5)");
    }

    [Fact]
    public void Render_NoLiveData_NeverPaintsASeverityColour_TrackOnlyPlusBadge()
    {
        // R3/R6: no LIVE data anywhere -> no arc, EVER, no matter what stale Severity/Percent the caller
        // happens to be carrying — AnyLive is the only gate. Never a grey/desaturated "last-known" arc,
        // never green by default.
        var state = new TrayIconState(AnyLive: false, Severity.Critical, 99m, Badge: true);
        using var bitmap = IconRenderer.Render(state, size: 32, highContrast: false);

        Assert.False(ContainsColor(bitmap, SevCrit), "no LIVE data must never paint a severity colour");
        Assert.False(ContainsColor(bitmap, SevOk), "no LIVE data must never default to green");
        Assert.True(ContainsColor(bitmap, TrayTrack), "the track must still render");
        Assert.True(ContainsColor(bitmap, TrayBadgeDisc), "the badge must be present");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_Live_PaintsTheWorstLiveSeverityColour(bool badge)
    {
        var state = new TrayIconState(AnyLive: true, Severity.Critical, 90m, Badge: badge);
        using var bitmap = IconRenderer.Render(state, size: 32, highContrast: false);

        Assert.True(ContainsColor(bitmap, SevCrit), "a LIVE critical reading must paint the sev/crit colour on the arc");
        Assert.Equal(badge, ContainsColor(bitmap, TrayBadgeDisc));
    }

    [Fact]
    public void Render_HealthyLive_PaintsSevOk_NeverInPopupButAlwaysInTray()
    {
        // §3: sev/ok is "tray arc only" — a fully-LIVE, healthy worst metric still paints the arc green.
        var state = new TrayIconState(AnyLive: true, Severity.Normal, 12m, Badge: false);
        using var bitmap = IconRenderer.Render(state, size: 32, highContrast: false);

        Assert.True(ContainsColor(bitmap, SevOk));
    }

    [Fact]
    public void Render_ZeroPercentLive_ShowsTrackOnly_NoArtificialMinimum()
    {
        var state = new TrayIconState(AnyLive: true, Severity.Normal, 0m, Badge: false);
        using var bitmap = IconRenderer.Render(state, size: 32, highContrast: false);

        // 0% LIVE is honest: track only, no fill arc at all (never an artificial minimum sliver, §2.5).
        Assert.False(ContainsColor(bitmap, SevOk));
        Assert.True(ContainsColor(bitmap, TrayTrack));
    }

    [Theory]
    [InlineData(150)]   // above the domain's 0..100 range
    [InlineData(-40)]   // below it
    public void Render_OutOfRangePercent_ClampsRatherThanThrowingOrGarbling(decimal percent)
    {
        // UsageViewBuilder's own IsLive guard means a real WindowView.Percent is always 0..100, but the
        // renderer must not trust that blindly — a corrupted/adversarial TrayIconState must clamp, not throw
        // or draw a nonsensical multi-lap sweep.
        var state = new TrayIconState(AnyLive: true, Severity.Critical, percent, Badge: false);

        using var bitmap = IconRenderer.Render(state, size: 32, highContrast: false);

        Assert.Equal(32, bitmap.Width);
        Assert.True(ContainsColor(bitmap, TrayTrack) || ContainsColor(bitmap, SevCrit));
    }

    [Fact]
    public void Render_HighContrast_UsesSystemColoursNotThemeColours()
    {
        var state = new TrayIconState(AnyLive: true, Severity.Warning, 85m, Badge: true);
        using var bitmap = IconRenderer.Render(state, size: 32, highContrast: true);

        Assert.True(ContainsColor(bitmap, SystemColors.Window));
        Assert.True(ContainsColor(bitmap, SystemColors.WindowText));
        Assert.False(ContainsColor(bitmap, SevWarn));
        Assert.False(ContainsColor(bitmap, TrayTile));
        Assert.False(ContainsColor(bitmap, TrayTrack));
    }

    private static bool ContainsColor(Bitmap bitmap, Color target, int tolerance = 6)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A < 200)
                {
                    continue; // ignore transparent/anti-aliased edge pixels
                }

                if (Math.Abs(pixel.R - target.R) <= tolerance &&
                    Math.Abs(pixel.G - target.G) <= tolerance &&
                    Math.Abs(pixel.B - target.B) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
