using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Proves the aggregate records compose the metric contract correctly — in particular that
/// sibling metrics degrade INDEPENDENTLY (DESIGN.md §3): one non-Available metric never
/// poisons another within the same window or snapshot.
/// </summary>
public sealed class DomainModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 07, 19, 14, 02, 00, TimeSpan.Zero);

    [Fact]
    public void SiblingMetrics_WithinAWindow_DegradeIndependently()
    {
        // UsedPercent is a live observation while ResetsAt is unavailable — each holds its
        // own state; the Unavailable ResetsAt does not force UsedPercent to n/a.
        var window = new UsageWindow(
            WindowMinutes: 10080,
            Label: WindowClassifier.Label(10080),
            UsedPercent: Metric.Available(73m, Now),
            ResetsAt: Metric.Unavailable<DateTimeOffset>("reset-passed"));

        Assert.Equal(MetricState.Available, window.UsedPercent.State);
        Assert.Equal(73m, window.UsedPercent.Value);
        Assert.Equal(MetricState.Unavailable, window.ResetsAt.State);
        Assert.Equal("reset-passed", window.ResetsAt.ReasonCode);
        Assert.Equal("Weekly", window.Label);
    }

    [Fact]
    public void SiblingMetrics_AcrossASnapshot_DegradeIndependently()
    {
        // A live weekly window sits alongside a NotApplicable 5h window, an Unavailable credits
        // balance and an Available plan type — four independent states in one snapshot.
        var weekly = new UsageWindow(
            WindowMinutes: 10080,
            Label: WindowClassifier.Label(10080),
            UsedPercent: Metric.Available(73m, Now),
            ResetsAt: Metric.Available(Now.AddDays(3), Now));

        var fiveHour = new UsageWindow(
            WindowMinutes: 300,
            Label: WindowClassifier.Label(300),
            UsedPercent: Metric.NotApplicable<decimal>("not-reported"),
            ResetsAt: Metric.NotApplicable<DateTimeOffset>("not-reported"));

        var snapshot = new ProviderSnapshot(
            ProviderId: "codex",
            FetchedAt: Now,
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: [weekly, fiveHour],
            CreditsBalance: Metric.Unavailable<decimal>("not-reported"),
            PlanType: Metric.Available("pro", Now));

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        Assert.Equal(MetricState.Available, snapshot.Windows[0].UsedPercent.State);
        Assert.Equal(MetricState.NotApplicable, snapshot.Windows[1].UsedPercent.State);
        Assert.Equal("not-reported", snapshot.Windows[1].UsedPercent.ReasonCode);
        Assert.Equal(MetricState.Unavailable, snapshot.CreditsBalance.State);
        Assert.Equal(MetricState.Available, snapshot.PlanType.State);
        Assert.Equal("pro", snapshot.PlanType.Value);
    }

    [Fact]
    public void UnavailableSnapshot_StillCarriesReasonAndCards()
    {
        // A provider card never disappears: an Unavailable source is still a full snapshot
        // whose metrics are honest n/a with reasons.
        var snapshot = new ProviderSnapshot(
            ProviderId: "codex",
            FetchedAt: Now,
            Status: SourceStatus.Unavailable,
            StatusReasonCode: "no-sessions-dir",
            Windows: [],
            CreditsBalance: Metric.Unavailable<decimal>("no-sessions-dir"),
            PlanType: Metric.Unavailable<string>("no-sessions-dir"));

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-sessions-dir", snapshot.StatusReasonCode);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(MetricState.Unavailable, snapshot.CreditsBalance.State);
    }

    [Fact]
    public void LastKnownReading_RetainsHistory_SeparateFromLiveMetrics()
    {
        // The DATED store holds a plain observed value + its observation and reset times —
        // truthful history, never a live Metric.
        var reading = new LastKnownReading(
            ProviderId: "claude",
            WindowMinutes: 10080,
            UsedPercent: 61m,
            ObservedAt: Now,
            ResetsAtAtObservation: Now.AddDays(2));

        Assert.Equal("claude", reading.ProviderId);
        Assert.Equal(10080, reading.WindowMinutes);
        Assert.Equal(61m, reading.UsedPercent);
        Assert.Equal(Now, reading.ObservedAt);
        Assert.Equal(Now.AddDays(2), reading.ResetsAtAtObservation);
    }
}
