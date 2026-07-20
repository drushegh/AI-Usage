using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Proves the freshness seam reads "now" only through an injected <see cref="TimeProvider"/>,
/// so it is fully testable with a fake clock (DESIGN.md §5; dotnet-development standard).
/// </summary>
public sealed class MetricFreshnessTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 07, 19, 14, 00, 00, TimeSpan.Zero);

    [Fact]
    public void Age_OfAvailableMetric_IsMeasuredAgainstTheInjectedClock()
    {
        var clock = new FakeTimeProvider(ObservedAt);
        var metric = Metric.Available(42m, ObservedAt);

        Assert.Equal(TimeSpan.Zero, MetricFreshness.Age(metric, clock));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(TimeSpan.FromMinutes(3), MetricFreshness.Age(metric, clock));
    }

    [Fact]
    public void Age_OfNotApplicableMetric_IsNull()
    {
        var clock = new FakeTimeProvider(ObservedAt.AddMinutes(5));
        var metric = Metric.NotApplicable<decimal>("not-reported");

        Assert.Null(MetricFreshness.Age(metric, clock));
    }

    [Fact]
    public void Age_OfUnavailableMetric_IsNull()
    {
        var clock = new FakeTimeProvider(ObservedAt.AddMinutes(5));
        var metric = Metric.Unavailable<decimal>("throttled");

        Assert.Null(MetricFreshness.Age(metric, clock));
    }

    [Fact]
    public void Age_UsesUtcNowFromProvider_NotWallClock()
    {
        // The clock is fixed far from real "now"; the computed age depends only on the
        // provider, proving no hidden DateTimeOffset.UtcNow call leaks in.
        var clock = new FakeTimeProvider(ObservedAt.AddHours(10));
        var metric = Metric.Available(1m, ObservedAt);

        Assert.Equal(TimeSpan.FromHours(10), MetricFreshness.Age(metric, clock));
    }
}
