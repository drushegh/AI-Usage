using System.Drawing.Imaging;
using System.IO;
using AIUsage.Core;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Manual eyeball tool for the Twin-Stop Gauge tray instrument (VISUAL-IDENTITY.md §2.5/§4.5/§6.6 — "never
/// ship unviewed"). A no-op by default (gated on <c>AIUSAGE_ICON_DUMP_DIR</c>, mirroring the
/// <c>AIUSAGE_SELFTEST_PNG</c> convention <c>App</c>'s popup/settings self-tests already use) so it never
/// costs anything in a normal <c>dotnet test</c> run. Set the env var to a directory and run:
/// <c>dotnet test --filter Render_DumpsSamplePngsForManualReview</c> to render every truth-table row at
/// every DPI-scaled size to PNGs for a human to look at.
/// </summary>
public sealed class IconRenderDump
{
    [Fact]
    public void Render_DumpsSamplePngsForManualReview()
    {
        var dir = Environment.GetEnvironmentVariable("AIUSAGE_ICON_DUMP_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return; // not requested — this test is a manual tool, not part of the asserted suite
        }

        Directory.CreateDirectory(dir);

        // One representative state per truth-table row (VISUAL-IDENTITY.md §4.5), keyed by a filename stem.
        var states = new (string Name, TrayIconState State)[]
        {
            ("row1-live-live-healthy", new TrayIconState(AnyLive: true, Severity.Normal, 42m, Badge: false)),
            ("row1-live-live-warn", new TrayIconState(AnyLive: true, Severity.Warning, 82m, Badge: false)),
            ("row1-live-live-crit", new TrayIconState(AnyLive: true, Severity.Critical, 96m, Badge: false)),
            ("row2-3-live-plus-incomplete", new TrayIconState(AnyLive: true, Severity.Warning, 88m, Badge: true)),
            ("row4-6-no-live-data", TrayIconState.NoData),
            ("live-zero-percent", new TrayIconState(AnyLive: true, Severity.Normal, 0m, Badge: false)),
        };

        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            foreach (var (name, state) in states)
            {
                using var bitmap = IconRenderer.Render(state, size, highContrast: false);
                bitmap.Save(Path.Combine(dir, $"{name}-{size}px.png"), ImageFormat.Png);
            }
        }

        // High Contrast, one representative frame per size.
        foreach (var size in new[] { 16, 32 })
        {
            using var bitmap = IconRenderer.Render(
                new TrayIconState(AnyLive: true, Severity.Critical, 96m, Badge: true), size, highContrast: true);
            bitmap.Save(Path.Combine(dir, $"high-contrast-{size}px.png"), ImageFormat.Png);
        }
    }
}
