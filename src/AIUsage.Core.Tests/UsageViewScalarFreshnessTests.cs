using Xunit;

using static AIUsage.Core.Tests.ViewTestData;

namespace AIUsage.Core.Tests;

/// <summary>
/// Credits and plan are provider-level scalars, but they carry the same staleness risk as windows: the raw
/// builder copied them straight from the snapshot, so an ancient Codex event or a stale-but-Ok snapshot
/// would show a stale balance/plan as current while its windows correctly degraded (review Fix 4). These
/// tests pin the freshness classification the builder now applies to both scalars.
/// </summary>
public sealed class UsageViewScalarFreshnessTests
{
    private static readonly DisplayConfig Config = DisplayConfig.Default; // 20-min Codex TTL

    private static ProviderSnapshot OkWithScalars(decimal credits, string plan, DateTimeOffset observedAt)
        => new(
            Codex,
            observedAt,
            SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: new[] { Window(10080, 42m, observedAt, observedAt.AddHours(5)) },
            CreditsBalance: Metric.Available(credits, observedAt),
            PlanType: Metric.Available(plan, observedAt));

    [Fact]
    public void FreshSnapshot_CreditsAndPlan_StayAvailable()
    {
        var clock = new FakeTimeProvider(Base.AddMinutes(5)); // within the 20-min Codex TTL
        var view = UsageViewBuilder.Build(Map(OkWithScalars(12.4m, "Pro", Base)), new LastKnownReadingStore(), Config, clock);
        var provider = view.Providers.Single();

        Assert.Equal(MetricState.Available, provider.CreditsBalance.State);
        Assert.Equal(12.4m, provider.CreditsBalance.Value);
        Assert.Equal(MetricState.Available, provider.PlanType.State);
        Assert.Equal("Pro", provider.PlanType.Value);
    }

    [Fact]
    public void StaleSnapshot_CreditsAndPlan_DegradeToNaStale_NotShownAsCurrent()
    {
        // The observation is past the Codex TTL — the windows go n/a and the scalars must too (Fix 4).
        var clock = new FakeTimeProvider(Base.AddMinutes(25));
        var view = UsageViewBuilder.Build(Map(OkWithScalars(12.4m, "Pro", Base)), new LastKnownReadingStore(), Config, clock);
        var provider = view.Providers.Single();

        Assert.NotEqual(MetricState.Available, provider.CreditsBalance.State);
        Assert.Equal("stale", provider.CreditsBalance.ReasonCode);
        Assert.NotEqual(MetricState.Available, provider.PlanType.State);
        Assert.Equal("stale", provider.PlanType.ReasonCode);
    }

    [Fact]
    public void FutureDatedScalars_DegradeToNaStale()
    {
        // A scalar observed in the future (beyond skew) is not fresh either.
        var clock = new FakeTimeProvider(Base);
        var view = UsageViewBuilder.Build(
            Map(OkWithScalars(12.4m, "Pro", Base.AddMinutes(10))), new LastKnownReadingStore(), Config, clock);
        var provider = view.Providers.Single();

        Assert.Equal("stale", provider.CreditsBalance.ReasonCode);
        Assert.Equal("stale", provider.PlanType.ReasonCode);
    }

    [Fact]
    public void UnavailableSource_KeepsScalarsOwnReason_NotOverwrittenWithStale()
    {
        // A source that is not Ok already publishes n/a scalars with their own reason — preserve it.
        var clock = new FakeTimeProvider(Base);
        var view = UsageViewBuilder.Build(
            Map(UnavailableSnapshot(Codex, Base, "no-sessions-dir")), new LastKnownReadingStore(), Config, clock);
        var provider = view.Providers.Single();

        Assert.Equal("no-sessions-dir", provider.CreditsBalance.ReasonCode);
        Assert.Equal("no-sessions-dir", provider.PlanType.ReasonCode);
    }
}
