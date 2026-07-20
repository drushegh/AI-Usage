using Xunit;

using static AIUsage.Core.Tests.ViewTestData;

namespace AIUsage.Core.Tests;

/// <summary>
/// The LIVE / DATED / N-A machine (DESIGN.md §5; tasks T8, T17). Proves a window renders as a fresh
/// number ONLY within the TTL and before its reset, falls back to truthful DATED history when a prior
/// good reading exists and its window has not reset, and otherwise reads explicit n/a — never zero,
/// never an estimate. Every decision is driven by a fake clock.
/// </summary>
public sealed class UsageViewFreshnessTests
{
    private static readonly DisplayConfig Config = DisplayConfig.Default; // 20-min TTL, 80/90

    // ---- IsLive predicate (T8) ----

    [Fact]
    public void IsLive_WithinTtl_UnresetInRange_IsTrue()
    {
        var clock = new FakeTimeProvider(Base.AddMinutes(10));
        var window = Window(10080, 42m, Base, Base.AddHours(5));

        Assert.True(UsageViewBuilder.IsLive(window, Config, clock));
    }

    [Fact]
    public void IsLive_AtExactTtlBoundary_IsTrue_OneTickPast_IsFalse()
    {
        var window = Window(10080, 42m, Base, Base.AddHours(5));

        var atBoundary = new FakeTimeProvider(Base + Config.CodexCurrentTtl);
        Assert.True(UsageViewBuilder.IsLive(window, Config, atBoundary));

        atBoundary.Advance(TimeSpan.FromTicks(1));
        Assert.False(UsageViewBuilder.IsLive(window, Config, atBoundary));
    }

    [Fact]
    public void IsLive_ResetPassed_IsFalse_EvenWithinTtl()
    {
        // Observed 2 minutes ago (well within the 20-min TTL) but the reset boundary already passed.
        var clock = new FakeTimeProvider(Base.AddMinutes(2));
        var window = Window(10080, 42m, Base, Base.AddMinutes(1));

        Assert.False(UsageViewBuilder.IsLive(window, Config, clock));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(150)]
    public void IsLive_OutOfRange_IsFalse(decimal percent)
    {
        var clock = new FakeTimeProvider(Base);
        var window = Window(10080, percent, Base, Base.AddHours(5));

        Assert.False(UsageViewBuilder.IsLive(window, Config, clock));
    }

    [Fact]
    public void IsLive_NonAvailableUsedPercent_IsFalse()
    {
        var clock = new FakeTimeProvider(Base);
        var window = new UsageWindow(10080, "Weekly",
            UsedPercent: Metric.Unavailable<decimal>("source-changed"),
            ResetsAt: Metric.Available(Base.AddHours(5), Base));

        Assert.False(UsageViewBuilder.IsLive(window, Config, clock));
    }

    [Fact]
    public void IsLive_FutureObservationBeyondSkew_IsFalse()
    {
        // Observed 5 minutes in the FUTURE (beyond the 2-min skew) → not a real fresh reading (review Fix 2).
        var clock = new FakeTimeProvider(Base);
        var window = Window(10080, 42m, Base.AddMinutes(5), Base.AddHours(5));

        Assert.False(UsageViewBuilder.IsLive(window, Config, clock));
    }

    [Fact]
    public void IsLive_MissingReset_IsFalse_EvenWhenFreshAndInRange()
    {
        // Fresh, in-range, but resets_at is n/a → LIVE now requires a coherent FUTURE reset boundary (Fix 3).
        var clock = new FakeTimeProvider(Base.AddMinutes(1));
        var window = new UsageWindow(10080, "Weekly",
            UsedPercent: Metric.Available(42m, Base),
            ResetsAt: Metric.NotApplicable<DateTimeOffset>("not-reported"));

        Assert.False(UsageViewBuilder.IsLive(window, Config, clock));
    }

    // ---- Build: LIVE ----

    [Fact]
    public void WithinTtl_RendersLive_WithPercentAndObservedAt()
    {
        var clock = new FakeTimeProvider(Base.AddMinutes(5));
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 42m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.Live, window.DisplayState);
        Assert.Equal(42m, window.Percent);
        Assert.Equal(Base, window.ObservedAt);
        Assert.Null(window.ReasonCode);
    }

    // ---- Build: DATED ----

    [Fact]
    public void PastTtl_WindowUnreset_WithPriorReading_RendersDated_CarryingObservedAt()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 61m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot); // record the good reading while fresh

        clock.Advance(TimeSpan.FromMinutes(21)); // past the 20-min TTL, still long before the 5h reset

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.Dated, window.DisplayState);
        Assert.Equal(61m, window.Percent);          // the truthful historical value
        Assert.Equal(Base, window.ObservedAt);       // "as of T" — the original observation time
        Assert.Null(window.ReasonCode);
    }

    [Fact]
    public void ProviderGoesUnavailable_ButUnresetHistoryRemains_RendersDated()
    {
        var clock = new FakeTimeProvider(Base);
        var live = OkSnapshot(Codex, Base, Window(10080, 55m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(live);

        clock.Advance(TimeSpan.FromMinutes(30));
        // The source is now fully Unavailable (no windows) — history must survive the snapshot swap.
        var down = UnavailableSnapshot(Codex, Base.AddMinutes(30), "no-sessions-dir");

        var view = UsageViewBuilder.Build(Map(down), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.Dated, window.DisplayState);
        Assert.Equal(55m, window.Percent);
        Assert.Equal(Base, window.ObservedAt);
    }

    // ---- Build: N-A ----

    [Fact]
    public void PastResets_WithNoNewerReading_RendersNa_ResetPassed_NeverZero()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 73m, Base, Base.AddMinutes(30)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        clock.Advance(TimeSpan.FromMinutes(31)); // past the reset boundary (and the TTL)

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.NA, window.DisplayState);
        Assert.Equal("reset-passed", window.ReasonCode);
        Assert.Null(window.Percent); // the whole point: n/a, NEVER a zero
    }

    [Fact]
    public void PastTtl_NoPriorReading_RendersNa_NoRecentEvent()
    {
        // No RecordFrom: there is no retained history to fall back to.
        var clock = new FakeTimeProvider(Base.AddMinutes(21));
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 42m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.NA, window.DisplayState);
        Assert.Equal("no-recent-event", window.ReasonCode);
        Assert.Null(window.Percent);
    }

    [Fact]
    public void OutOfRangeValue_RendersNa_OutOfRange_AndIsNeverRetained()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 150m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot); // must skip the garbage reading

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.NA, window.DisplayState);
        Assert.Equal("out-of-range", window.ReasonCode);
        Assert.Null(window.Percent);
        Assert.False(store.TryGet(Codex, 10080, out _));
    }

    [Fact]
    public void FreshWindow_NoReset_NoHistory_RendersNa_ResetUnknown_NeverZero()
    {
        // A fresh, in-range window whose reset boundary is unknown cannot be LIVE (Fix 3), and RecordFrom
        // never retains a reset-less window, so it renders explicit n/a — never a zero, never a live number.
        var clock = new FakeTimeProvider(Base.AddMinutes(1));
        var window = new UsageWindow(10080, "Weekly",
            UsedPercent: Metric.Available(42m, Base),
            ResetsAt: Metric.NotApplicable<DateTimeOffset>("not-reported"));
        var snapshot = OkSnapshot(Codex, Base, window);
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot); // must skip it (no coherent reset)

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var w = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.NA, w.DisplayState);
        Assert.Equal("reset-unknown", w.ReasonCode);
        Assert.Null(w.Percent);
        Assert.False(store.TryGet(Codex, 10080, out _)); // never retained without a reset
    }

    // ---- Re-reading the same event does not renew liveness ----

    [Fact]
    public void RereadingSameEvent_DoesNotRenewLiveness()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 40m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.Equal(DisplayState.Live, UsageViewBuilder.Build(Map(snapshot), store, Config, clock).Window(Codex, 10080).DisplayState);

        // Advance past the TTL and "re-read" the SAME event (same embedded ObservedAt). Liveness is
        // anchored to ObservedAt, so re-recording/re-building must NOT make it Live again.
        clock.Advance(TimeSpan.FromMinutes(2)); // now +21
        store.RecordFrom(snapshot);
        Assert.Equal(DisplayState.Dated, UsageViewBuilder.Build(Map(snapshot), store, Config, clock).Window(Codex, 10080).DisplayState);
    }

    [Fact]
    public void UnavailableSource_WithAContrivedAvailableWindow_IsNotLive()
    {
        // Even if an Unavailable snapshot somehow carried an Available window, LIVE requires an Ok
        // source (DESIGN.md §5 rule 1) — so it degrades to n/a, never a trusted live number.
        var clock = new FakeTimeProvider(Base);
        var window = Window(10080, 42m, Base, Base.AddHours(5));
        var snapshot = new ProviderSnapshot(Codex, Base, SourceStatus.Unavailable, "source-changed",
            [window], Metric.Unavailable<decimal>("source-changed"), Metric.Unavailable<string>("source-changed"));
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        Assert.Equal(DisplayState.NA, view.Window(Codex, 10080).DisplayState);
    }
}
