using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Proves the <see cref="Metric{T}"/> invariants — the accuracy contract expressed in types
/// (DESIGN.md §3). These are the acceptance tests for the tri-state metric.
/// </summary>
public sealed class MetricTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 07, 19, 14, 02, 00, TimeSpan.Zero);

    [Fact]
    public void Available_CarriesValueAndObservedAt_AndNoReason()
    {
        var metric = Metric.Available(42.5m, ObservedAt);

        Assert.Equal(MetricState.Available, metric.State);
        Assert.Equal(42.5m, metric.Value);
        Assert.Equal(ObservedAt, metric.ObservedAt);
        Assert.Null(metric.ReasonCode);
    }

    [Fact]
    public void Available_PreservesUnroundedDecimalOn0To100Scale()
    {
        // E1: the value is a 0–100 percent (never a 0–1 fraction) and is preserved unrounded.
        var metric = Metric.Available(28.0m, ObservedAt);

        Assert.Equal(MetricState.Available, metric.State);
        Assert.Equal(28.0m, metric.Value);
    }

    [Fact]
    public void Available_WithNullReferenceValue_Throws()
    {
        // You cannot construct an Available metric without a value: the only public path
        // requires one, and a null reference value is rejected at the factory.
        Assert.Throws<ArgumentNullException>(() => Metric.Available<string>(null!, ObservedAt));
    }

    [Fact]
    public void GenuineZeroPercent_IsRepresentableAsAvailable()
    {
        // A real 0% reading is legal and distinct from a missing metric — the ban is on
        // DEFAULTING to zero, not on a genuine observed zero.
        var metric = Metric.Available(0m, ObservedAt);

        Assert.Equal(MetricState.Available, metric.State);
        Assert.Equal(0m, metric.Value);
        Assert.Equal(ObservedAt, metric.ObservedAt);
    }

    [Fact]
    public void NotApplicable_CarriesReason_HasNoObservedAt_AndIsNotAvailable()
    {
        var metric = Metric.NotApplicable<decimal>("not-reported");

        Assert.Equal(MetricState.NotApplicable, metric.State);
        Assert.NotEqual(MetricState.Available, metric.State);
        Assert.Equal("not-reported", metric.ReasonCode);
        Assert.Null(metric.ObservedAt);
    }

    [Fact]
    public void Unavailable_CarriesReason_HasNoObservedAt_AndIsNotAvailable()
    {
        var metric = Metric.Unavailable<DateTimeOffset>("reset-passed");

        Assert.Equal(MetricState.Unavailable, metric.State);
        Assert.NotEqual(MetricState.Available, metric.State);
        Assert.Equal("reset-passed", metric.ReasonCode);
        Assert.Null(metric.ObservedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NonAvailable_WithoutReason_Throws(string? reason)
    {
        Assert.Throws<ArgumentException>(() => Metric.NotApplicable<decimal>(reason!));
        Assert.Throws<ArgumentException>(() => Metric.Unavailable<decimal>(reason!));
    }

    [Fact]
    public void MissingWindow_IsNotApplicable_NeverAZeroValuedAvailable()
    {
        // The load-bearing rule: a missing window is NotApplicable("not-reported"), which the
        // UI renders as explicit n/a — it is NOT an Available metric that happens to read 0%.
        var usedPercent = Metric.NotApplicable<decimal>("not-reported");

        Assert.Equal(MetricState.NotApplicable, usedPercent.State);
        Assert.Equal("not-reported", usedPercent.ReasonCode);
        Assert.False(usedPercent.State == MetricState.Available,
            "a missing window must never be modelled as a zero-valued Available metric");
    }

    [Fact]
    public void Metrics_HaveValueEquality()
    {
        // Records give structural equality: identical state/value/time/reason compare equal;
        // any difference does not.
        var a = Metric.Available(61m, ObservedAt);
        var b = Metric.Available(61m, ObservedAt);
        var different = Metric.Available(62m, ObservedAt);
        var naA = Metric.NotApplicable<decimal>("not-reported");
        var naB = Metric.NotApplicable<decimal>("not-reported");

        Assert.Equal(a, b);
        Assert.NotEqual(a, different);
        Assert.Equal(naA, naB);
        Assert.NotEqual<Metric<decimal>>(a, naA);
    }
}
