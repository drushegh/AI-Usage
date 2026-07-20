using AIUsage.Core;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Smoke coverage for the icon renderer (tasks T10/T11): every render-model state produces a bitmap of
/// the requested size without throwing, at both a base and a high-DPI size. (The pixel-level §9 form
/// decision is the deferred E4 render test; this only asserts the render path is sound.)
/// </summary>
public sealed class IconRendererTests
{
    public static IEnumerable<object[]> States() =>
    [
        [Severity.Normal, false, false],   // plain safe disc
        [Severity.Warning, false, false],  // caution triangle
        [Severity.Critical, false, false], // critical square
        [Severity.Warning, true, false],   // severity shape + unknown badge
        [Severity.Normal, true, true],     // neutral all-unknown "?"
    ];

    [Theory]
    [MemberData(nameof(States))]
    public void Render_ProducesRequestedSize(Severity severity, bool unknown, bool allUnknown)
    {
        foreach (var size in new[] { 16, 32 })
        {
            using var bitmap = IconRenderer.Render(severity, unknown, allUnknown, size, lightTaskbar: false);

            Assert.NotNull(bitmap);
            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
        }
    }

    [Fact]
    public void Render_ClampsBelowSixteenToTheFloor()
    {
        using var bitmap = IconRenderer.Render(Severity.Normal, unknown: false, allUnknown: false, size: 4, lightTaskbar: true);

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
    }
}
