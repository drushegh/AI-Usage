using Xunit;

using static AIUsage.Core.Tests.ViewTestData;

namespace AIUsage.Core.Tests;

/// <summary>
/// The retained DATED history store (DESIGN.md §3, §5; task T17). Proves it is a plain, newest-wins
/// retention keyed by (provider, window), that it only ingests GOOD readings (Available + in-range +
/// coherent reset), and that it isolates providers — it decides nothing about display.
/// </summary>
public sealed class LastKnownReadingStoreTests
{
    [Fact]
    public void RecordFrom_RetainsAvailableInRangeWindowsWithAReset()
    {
        var store = new LastKnownReadingStore();
        store.RecordFrom(OkSnapshot(Codex, Base, Window(10080, 61m, Base, Base.AddHours(5))));

        Assert.True(store.TryGet(Codex, 10080, out var reading));
        Assert.Equal(61m, reading!.UsedPercent);
        Assert.Equal(Base, reading.ObservedAt);
        Assert.Equal(Base.AddHours(5), reading.ResetsAtAtObservation);
    }

    [Fact]
    public void RecordFrom_SkipsWindowsWithoutACoherentReset()
    {
        // Available percent but Unavailable reset → cannot bound DATED validity → not retained.
        var window = new UsageWindow(10080, "Weekly",
            UsedPercent: Metric.Available(61m, Base),
            ResetsAt: Metric.Unavailable<DateTimeOffset>("not-reported"));
        var store = new LastKnownReadingStore();
        store.RecordFrom(OkSnapshot(Codex, Base, window));

        Assert.False(store.TryGet(Codex, 10080, out _));
    }

    [Fact]
    public void RecordFrom_SkipsNonAvailableAndOutOfRangeWindows()
    {
        var store = new LastKnownReadingStore();
        store.RecordFrom(OkSnapshot(Codex, Base,
            new UsageWindow(300, "5h", Metric.NotApplicable<decimal>("not-reported"), Metric.NotApplicable<DateTimeOffset>("not-reported")),
            Window(10080, 150m, Base, Base.AddHours(5)))); // out of range

        Assert.False(store.TryGet(Codex, 300, out _));
        Assert.False(store.TryGet(Codex, 10080, out _));
    }

    [Fact]
    public void Record_NewerObservation_Wins()
    {
        var store = new LastKnownReadingStore();
        store.Record(new LastKnownReading(Codex, 10080, 40m, Base, Base.AddHours(5)));
        store.Record(new LastKnownReading(Codex, 10080, 55m, Base.AddMinutes(10), Base.AddHours(5)));

        Assert.True(store.TryGet(Codex, 10080, out var reading));
        Assert.Equal(55m, reading!.UsedPercent);
        Assert.Equal(Base.AddMinutes(10), reading.ObservedAt);
    }

    [Fact]
    public void Record_OlderObservation_DoesNotClobberNewer()
    {
        var store = new LastKnownReadingStore();
        store.Record(new LastKnownReading(Codex, 10080, 55m, Base.AddMinutes(10), Base.AddHours(5)));
        store.Record(new LastKnownReading(Codex, 10080, 40m, Base, Base.AddHours(5))); // arrives late, older

        Assert.True(store.TryGet(Codex, 10080, out var reading));
        Assert.Equal(55m, reading!.UsedPercent); // the newer observation is kept
    }

    [Fact]
    public void ForProvider_ReturnsOnlyThatProvidersReadings()
    {
        var store = new LastKnownReadingStore();
        store.Record(new LastKnownReading(Codex, 10080, 55m, Base, Base.AddHours(5)));
        store.Record(new LastKnownReading(Codex, 300, 20m, Base, Base.AddHours(1)));
        store.Record(new LastKnownReading(Claude, 10080, 61m, Base, Base.AddHours(5)));

        var codex = store.ForProvider(Codex);
        var claude = store.ForProvider(Claude);

        Assert.Equal(2, codex.Count);
        Assert.All(codex, r => Assert.Equal(Codex, r.ProviderId));
        Assert.Single(claude);
        Assert.Equal(61m, claude[0].UsedPercent);
    }

    [Fact]
    public void TryGet_MissingKey_IsFalse()
    {
        var store = new LastKnownReadingStore();
        Assert.False(store.TryGet(Codex, 10080, out var reading));
        Assert.Null(reading);
    }

    [Fact]
    public void RecordFrom_TwoWeeklyWindows_SameMinutes_DifferentScope_RetainIndependentHistory()
    {
        // The committed E1 payload carries weekly_all AND weekly_scoped BOTH at WindowMinutes 10080. With
        // the old minute-count key the second (equal ObservedAt) overwrites the first every cycle, so the
        // all-models history is destroyed by the per-model one (review P0-1). The composite key keeps them
        // apart.
        var all = new UsageWindow(10080, "Weekly",
            Metric.Available(61m, Base), Metric.Available(Base.AddHours(5), Base));
        var scoped = new UsageWindow(10080, "Fable wk",
            Metric.Available(84m, Base), Metric.Available(Base.AddHours(5), Base), ScopeId: "Fable");

        var store = new LastKnownReadingStore();
        store.RecordFrom(OkSnapshot(Claude, Base, all, scoped));

        Assert.True(store.TryGet(Claude, "10080", out var allReading));
        Assert.Equal(61m, allReading!.UsedPercent); // NOT clobbered to 84 by the scoped window
        Assert.True(store.TryGet(Claude, "10080:Fable", out var scopedReading));
        Assert.Equal(84m, scopedReading!.UsedPercent);
    }
}
