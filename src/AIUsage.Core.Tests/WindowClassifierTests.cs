using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Proves windows are classified by PROXIMITY of their minute-count to an anchor, never by
/// position (DESIGN.md banner / §4.1): 299/300 → FiveHour, 10079/10080 → Weekly, else Other.
/// </summary>
public sealed class WindowClassifierTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(299)]
    [InlineData(301)]
    [InlineData(285)] // low edge of the 10% band (270..330)
    [InlineData(330)] // high edge of the 10% band
    public void NearFiveHourAnchor_ClassifiesFiveHour(int windowMinutes)
        => Assert.Equal(WindowKind.FiveHour, WindowClassifier.Classify(windowMinutes));

    [Theory]
    [InlineData(10080)]
    [InlineData(10079)]
    [InlineData(10081)]
    [InlineData(9576)]  // low edge of the 10% band (9072..11088)
    [InlineData(10584)] // high edge of the 10% band
    public void NearWeeklyAnchor_ClassifiesWeekly(int windowMinutes)
        => Assert.Equal(WindowKind.Weekly, WindowClassifier.Classify(windowMinutes));

    [Theory]
    [InlineData(1440)] // daily — near neither anchor
    [InlineData(600)]  // 10h — outside the 5h band
    [InlineData(5040)] // half-week — outside both bands
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void NotNearAnyAnchor_ClassifiesOther(int windowMinutes)
        => Assert.Equal(WindowKind.Other, WindowClassifier.Classify(windowMinutes));

    [Theory]
    [InlineData(300, "5h")]
    [InlineData(299, "5h")]
    [InlineData(10080, "Weekly")]
    [InlineData(10079, "Weekly")]
    [InlineData(1440, "24h")] // Other, whole hours → "Nh"
    [InlineData(45, "45m")]   // Other, not whole hours → "Nm"
    public void Label_MatchesKind(int windowMinutes, string expected)
        => Assert.Equal(expected, WindowClassifier.Label(windowMinutes));
}
