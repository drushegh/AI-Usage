using AIUsage.Core;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Direct coverage of the mixed-state truth table (VISUAL-IDENTITY.md §4.5, rows R1–R6) against
/// <see cref="TrayIconState.Compute"/>. Most rows are exercised against hand-built <see cref="UsageView"/>
/// fixtures (the same shape <see cref="AIUsage.Core.UsageViewBuilder"/> produces) so every branch of
/// <c>Compute</c> is pinned precisely; a couple of the rows are also run through the REAL
/// <see cref="UsageViewBuilder"/> pipeline (from <see cref="ProviderSnapshot"/>s) so a drift between what
/// the builder actually emits and what this file assumes would fail loudly here too.
/// </summary>
public sealed class TrayIconStateTests
{
    private const string Claude = "claude";
    private const string Codex = "codex";
    private static readonly DateTimeOffset Now = new(2026, 07, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compute_ThrowsOnNullView() => Assert.Throws<ArgumentNullException>(() => TrayIconState.Compute(null!));

    [Fact]
    public void Row1_LiveLive_ArcFromWorstLive_NoBadge()
    {
        var view = View(
            unknown: false,
            allUnknown: false,
            overall: Severity.Normal,
            Provider(Claude, unknown: false, allUnknown: false, Severity.Normal, LiveWindow(Claude, 300, 42m, Severity.Normal)),
            Provider(Codex, unknown: false, allUnknown: false, Severity.Normal, LiveWindow(Codex, 300, 31m, Severity.Normal)));

        var state = TrayIconState.Compute(view);

        Assert.True(state.AnyLive);
        Assert.False(state.Badge);
        Assert.Equal(Severity.Normal, state.Severity);
        Assert.Equal(42m, state.Percent); // the worst (highest) LIVE percent drives both colour and length
    }

    [Fact]
    public void Row2_LiveDated_ArcFromLiveProviderOnly_Badge()
    {
        // The DATED reading is a historic 91% Warning — R1 requires it NEVER leak into the arc.
        var view = View(
            unknown: true,
            allUnknown: false,
            overall: Severity.Warning,
            Provider(Claude, unknown: false, allUnknown: false, Severity.Normal, LiveWindow(Claude, 300, 55m, Severity.Normal)),
            Provider(Codex, unknown: true, allUnknown: false, Severity.Warning, DatedWindow(Codex, 300, 91m, Severity.Warning)));

        var state = TrayIconState.Compute(view);

        Assert.True(state.AnyLive);
        Assert.True(state.Badge);
        Assert.Equal(Severity.Normal, state.Severity); // NOT Warning — the DATED 91% must never drive the arc
        Assert.Equal(55m, state.Percent);
    }

    [Fact]
    public void Row3_LiveNa_ArcFromLiveProviderOnly_Badge()
    {
        var view = View(
            unknown: true,
            allUnknown: false,
            overall: Severity.Normal,
            Provider(Claude, unknown: false, allUnknown: false, Severity.Normal, LiveWindow(Claude, 300, 20m, Severity.Normal)),
            Provider(Codex, unknown: true, allUnknown: true, Severity.Normal, NaWindow(Codex, 300)));

        var state = TrayIconState.Compute(view);

        Assert.True(state.AnyLive);
        Assert.True(state.Badge);
        Assert.Equal(20m, state.Percent);
    }

    [Fact]
    public void Row4_DatedDated_NoArc_Badge()
    {
        var view = View(
            unknown: true,
            allUnknown: true,
            overall: Severity.Warning,
            Provider(Claude, unknown: true, allUnknown: true, Severity.Normal, DatedWindow(Claude, 300, 70m, Severity.Normal)),
            Provider(Codex, unknown: true, allUnknown: false, Severity.Warning, DatedWindow(Codex, 300, 91m, Severity.Warning)));

        var state = TrayIconState.Compute(view);

        Assert.False(state.AnyLive);
        Assert.True(state.Badge);
    }

    [Fact]
    public void Row5_DatedNa_NoArc_Badge()
    {
        var view = View(
            unknown: true,
            allUnknown: true,
            overall: Severity.Normal,
            Provider(Claude, unknown: true, allUnknown: true, Severity.Normal, DatedWindow(Claude, 300, 65m, Severity.Normal)),
            Provider(Codex, unknown: true, allUnknown: true, Severity.Normal, NaWindow(Codex, 300)));

        var state = TrayIconState.Compute(view);

        Assert.False(state.AnyLive);
        Assert.True(state.Badge);
    }

    [Fact]
    public void Row6_NaNa_NoArc_Badge_NeverGreenByDefault()
    {
        var view = View(
            unknown: true,
            allUnknown: true,
            overall: Severity.Normal,
            Provider(Claude, unknown: true, allUnknown: true, Severity.Normal, NaWindow(Claude, 300)),
            Provider(Codex, unknown: true, allUnknown: true, Severity.Normal, NaWindow(Codex, 300)));

        var state = TrayIconState.Compute(view);

        Assert.False(state.AnyLive); // AnyLive is what actually gates the arc — this is the real "never green" guard
        Assert.True(state.Badge);
    }

    [Fact]
    public void Row6_PreFirstFetch_EmptyView_MatchesNoDataState()
    {
        var empty = new UsageView(Severity.Normal, Unknown: true, AllUnknown: true, Array.Empty<ProviderView>());

        Assert.Equal(TrayIconState.NoData, TrayIconState.Compute(empty));
    }

    [Fact]
    public void Compute_PicksTheHighestLivePercentAcrossProviders_NotJustTheFirstEncountered()
    {
        var view = View(
            unknown: false,
            allUnknown: false,
            overall: Severity.Critical,
            Provider(Claude, unknown: false, allUnknown: false, Severity.Normal, LiveWindow(Claude, 300, 10m, Severity.Normal)),
            Provider(Codex, unknown: false, allUnknown: false, Severity.Critical, LiveWindow(Codex, 300, 95m, Severity.Critical)));

        var state = TrayIconState.Compute(view);

        Assert.Equal(95m, state.Percent);
        Assert.Equal(Severity.Critical, state.Severity);
    }

    [Fact]
    public void Compute_RealUsageViewBuilder_FullyLiveBothProviders_NoBadge()
    {
        var clock = new FixedTimeProvider(Now);
        var snapshots = SnapshotMap(
            OkSnapshot(Claude, LiveUsageWindow(300, 30m)),
            OkSnapshot(Codex, LiveUsageWindow(300, 88m)));

        var lastKnown = new LastKnownReadingStore();
        foreach (var snapshot in snapshots.Values)
        {
            lastKnown.RecordFrom(snapshot);
        }

        var view = UsageViewBuilder.Build(snapshots, lastKnown, DisplayConfig.Default, clock);
        var state = TrayIconState.Compute(view);

        Assert.True(state.AnyLive);
        Assert.False(state.Badge);
        Assert.Equal(Severity.Warning, state.Severity); // 88% >= the default 80% warn threshold
        Assert.Equal(88m, state.Percent);
    }

    [Fact]
    public void Compute_RealUsageViewBuilder_NothingPublishedYet_MatchesNoDataState()
    {
        var clock = new FixedTimeProvider(Now);
        var view = UsageViewBuilder.Build(
            new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal),
            new LastKnownReadingStore(),
            DisplayConfig.Default,
            clock);

        Assert.Equal(TrayIconState.NoData, TrayIconState.Compute(view));
    }

    // ---- fixtures ----

    private static UsageView View(bool unknown, bool allUnknown, Severity overall, params ProviderView[] providers)
        => new(overall, unknown, allUnknown, providers);

    private static ProviderView Provider(string id, bool unknown, bool allUnknown, Severity severity, params WindowView[] windows)
        => new(
            ProviderId: id,
            FetchedAt: Now,
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: windows,
            CreditsBalance: Metric.NotApplicable<decimal>("not-reported"),
            PlanType: Metric.NotApplicable<string>("not-reported"),
            Severity: severity,
            Unknown: unknown,
            AllUnknown: allUnknown);

    private static WindowView LiveWindow(string providerId, int minutes, decimal percent, Severity severity)
        => new(
            ProviderId: providerId,
            WindowMinutes: minutes,
            Label: minutes.ToString(),
            DisplayState: DisplayState.Live,
            Percent: percent,
            ObservedAt: Now,
            ResetsAt: Metric.Available(Now.AddHours(2), Now),
            Severity: severity,
            ReasonCode: null);

    private static WindowView DatedWindow(string providerId, int minutes, decimal percent, Severity severity)
        => new(
            ProviderId: providerId,
            WindowMinutes: minutes,
            Label: minutes.ToString(),
            DisplayState: DisplayState.Dated,
            Percent: percent,
            ObservedAt: Now.AddHours(-1),
            ResetsAt: Metric.Available(Now.AddHours(1), Now.AddHours(-1)),
            Severity: severity,
            ReasonCode: null);

    private static WindowView NaWindow(string providerId, int minutes)
        => new(
            ProviderId: providerId,
            WindowMinutes: minutes,
            Label: minutes.ToString(),
            DisplayState: DisplayState.NA,
            Percent: null,
            ObservedAt: null,
            ResetsAt: Metric.Unavailable<DateTimeOffset>("not-reported"),
            Severity: Severity.Normal,
            ReasonCode: "not-reported");

    private static UsageWindow LiveUsageWindow(int minutes, decimal percent)
        => new(minutes, minutes.ToString(), Metric.Available(percent, Now), Metric.Available(Now.AddHours(2), Now));

    private static ProviderSnapshot OkSnapshot(string providerId, params UsageWindow[] windows)
        => new(
            ProviderId: providerId,
            FetchedAt: Now,
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: windows,
            CreditsBalance: Metric.NotApplicable<decimal>("not-reported"),
            PlanType: Metric.NotApplicable<string>("not-reported"));

    private static IReadOnlyDictionary<string, ProviderSnapshot> SnapshotMap(params ProviderSnapshot[] snapshots)
    {
        var map = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            map[snapshot.ProviderId] = snapshot;
        }

        return map;
    }

    /// <summary>A fixed clock so the real-builder integration tests stay deterministic (no hidden UtcNow leak).</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
