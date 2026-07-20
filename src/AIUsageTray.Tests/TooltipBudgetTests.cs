using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers the tooltip line-budgeting helper (review P2-17). The load-bearing guarantee: when two provider
/// lines don't both fit, NEITHER is dropped whole — each keeps at least a floor of characters (abbreviated),
/// because a silently-dropped second provider line would read as implied-safe.
/// </summary>
public sealed class TooltipBudgetTests
{
    [Fact]
    public void EverythingFits_ReturnedVerbatim()
    {
        var lines = new[] { "Codex: Weekly 4% used", "Claude: 5h 12% used" };

        var result = TooltipBudget.Fit(lines, maxLength: 127, perLineFloor: 40);

        Assert.Equal("Codex: Weekly 4% used\nClaude: 5h 12% used", result);
    }

    [Fact]
    public void TwoLongLines_BothSurvive_AtLeastAbbreviated()
    {
        // Two long provider lines that cannot both fit at full length within the real 127-char cap.
        var claude = "Claude: 5h 34.7% used · Weekly 61.2% used · Fable wk 83.9% used · cr $8.75";
        var codex = "Codex: 5h 4.0% used · Weekly 82.4% used · 24h 51.9% used · cr $12.40";
        var lines = new[] { claude, codex };

        var result = TooltipBudget.Fit(lines, maxLength: 127, perLineFloor: 40);

        Assert.True(result.Length <= 127, $"Result length {result.Length} exceeded the cap.");
        var parts = result.Split('\n');
        Assert.Equal(2, parts.Length);
        // Neither line is dropped: each retains its provider label and its floor of characters.
        Assert.StartsWith("Claude:", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("Codex:", parts[1], StringComparison.Ordinal);
        Assert.True(parts[0].Length >= 40, $"Claude line too short: {parts[0].Length}");
        Assert.True(parts[1].Length >= 40, $"Codex line too short: {parts[1].Length}");
    }

    [Fact]
    public void ShortLine_LeavesMoreBudgetForTheLong_One()
    {
        var shortLine = "Codex: off";
        var longLine = new string('x', 200);
        var lines = new[] { shortLine, longLine };

        var result = TooltipBudget.Fit(lines, maxLength: 127, perLineFloor: 40);
        var parts = result.Split('\n');

        Assert.True(result.Length <= 127);
        Assert.Equal(shortLine, parts[0]);                 // short line kept whole
        Assert.True(parts[1].Length > 40, "The long line should reclaim the short line's unused budget.");
    }

    [Fact]
    public void NeverExceedsCap_EvenWhenTiny()
    {
        var lines = new[] { new string('a', 50), new string('b', 50) };

        var result = TooltipBudget.Fit(lines, maxLength: 9, perLineFloor: 40);

        Assert.True(result.Length <= 9, $"Result length {result.Length} exceeded the cap.");
    }

    [Fact]
    public void EmptyOrNonPositive_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TooltipBudget.Fit(Array.Empty<string>(), 127, 40));
        Assert.Equal(string.Empty, TooltipBudget.Fit(new[] { "abc" }, 0, 40));
    }
}
