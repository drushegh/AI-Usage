using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers <see cref="UsageFormat.FriendlyReason"/>'s reason-code vocabulary (DESIGN.md §5). Focused on the
/// codes whose exact wording is load-bearing for the honest n/a story — in particular "gated", the
/// post-restart "waiting on the cadence gate" state that must read as a benign wait and NEVER as a failed
/// fetch (review NEW-3).
/// </summary>
public sealed class UsageFormatTests
{
    [Fact]
    public void FriendlyReason_Gated_ReadsAsWaiting_NotAnError()
    {
        // A persisted, still-closed gate with no prior reading returns "gated" (review NEW-3): the UI must
        // present it as a benign wait, never conflated with the genuine-failure "fetch error".
        Assert.Equal("waiting", UsageFormat.FriendlyReason("gated"));
        Assert.NotEqual(UsageFormat.FriendlyReason("fetch-error"), UsageFormat.FriendlyReason("gated"));
    }

    [Theory]
    [InlineData("fetch-error", "fetch error")]
    [InlineData("disabled", "disabled")]
    [InlineData("source-changed", "source changed")]
    public void FriendlyReason_KnownCodes_ExpandToHumanText(string code, string expected)
        => Assert.Equal(expected, UsageFormat.FriendlyReason(code));

    [Fact]
    public void FriendlyReason_UnknownCode_DegradesToHyphenSplitWords_NeverBlank()
        => Assert.Equal("some new code", UsageFormat.FriendlyReason("some-new-code"));
}
