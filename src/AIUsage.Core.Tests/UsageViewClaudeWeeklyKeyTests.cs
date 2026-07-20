using Xunit;

using static AIUsage.Core.Tests.ViewTestData;

namespace AIUsage.Core.Tests;

/// <summary>
/// The confirmed P0-1 wrong-number path: Claude's <c>weekly_all</c> and <c>weekly_scoped</c> windows both
/// carry <c>WindowMinutes = 10080</c>, so keying DATED history / severity by the bare minute-count made one
/// window's percentage surface under the other's label on staleness. These tests prove the composite window
/// key (<see cref="UsageWindow.Key"/>) keeps the two windows fully independent through the view builder.
/// </summary>
public sealed class UsageViewClaudeWeeklyKeyTests
{
    private static readonly DisplayConfig Config = DisplayConfig.Default; // 210s Claude TTL, 80/90 thresholds

    private static UsageWindow Weekly(decimal percent, string? scopeId, string label)
        => new(10080, label, Metric.Available(percent, Base), Metric.Available(Base.AddDays(4), Base), scopeId);

    private static WindowView WeeklyLabelled(UsageView view, string label)
        => Assert.Single(view.Providers.Single().Windows, w => w.Label == label);

    [Fact]
    public void TwoWeeklyWindows_ShareMinutes_ButClassifyAndDateIndependently()
    {
        // weekly_all at 61% (safe) and per-model weekly_scoped "Fable" at 85% (warning): the exact E1 shape.
        var snapshot = OkSnapshot(Claude, Base,
            Weekly(61m, null, "Weekly"),
            Weekly(85m, "Fable", "Fable wk"));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        // While both are LIVE the scoped window drives Warning and the all window stays Normal — no bleed.
        var live = UsageViewBuilder.Build(Map(snapshot), store, Config, new FakeTimeProvider(Base.AddSeconds(30)));
        Assert.Equal(DisplayState.Live, WeeklyLabelled(live, "Weekly").DisplayState);
        Assert.Equal(Severity.Normal, WeeklyLabelled(live, "Weekly").Severity);
        Assert.Equal(DisplayState.Live, WeeklyLabelled(live, "Fable wk").DisplayState);
        Assert.Equal(Severity.Warning, WeeklyLabelled(live, "Fable wk").Severity);

        // Past Claude's 210s TTL both fall to DATED, each carrying its OWN retained percent — the all window
        // is NOT shown the scoped window's 85% (the old collision), and severities stay independent.
        var dated = UsageViewBuilder.Build(Map(snapshot), store, Config, new FakeTimeProvider(Base.AddSeconds(240)));
        Assert.Equal(DisplayState.Dated, WeeklyLabelled(dated, "Weekly").DisplayState);
        Assert.Equal(61m, WeeklyLabelled(dated, "Weekly").Percent);
        Assert.Equal(Severity.Normal, WeeklyLabelled(dated, "Weekly").Severity);

        Assert.Equal(DisplayState.Dated, WeeklyLabelled(dated, "Fable wk").DisplayState);
        Assert.Equal(85m, WeeklyLabelled(dated, "Fable wk").Percent); // NOT 61 — history did not collapse
        Assert.Equal(Severity.Warning, WeeklyLabelled(dated, "Fable wk").Severity);
    }

    [Fact]
    public void ScopedWindowGoesStale_DoesNotOverwriteWeeklyAllHistory()
    {
        // Record both while LIVE, then publish a snapshot that reports ONLY weekly_all fresh (the scoped
        // window dropped out). The scoped DATED history must survive under its own key and not be read as
        // the all-models weekly, and weekly_all must render its own fresh number.
        var seed = OkSnapshot(Claude, Base, Weekly(61m, null, "Weekly"), Weekly(85m, "Fable", "Fable wk"));
        var store = new LastKnownReadingStore();
        store.RecordFrom(seed);

        var clock = new FakeTimeProvider(Base.AddSeconds(120));
        var onlyAll = OkSnapshot(Claude, Base.AddSeconds(120), Weekly(64m, null, "Weekly"));
        store.RecordFrom(onlyAll);

        var view = UsageViewBuilder.Build(Map(onlyAll), store, Config, clock);

        var all = WeeklyLabelled(view, "Weekly");
        Assert.Equal(DisplayState.Live, all.DisplayState);
        Assert.Equal(64m, all.Percent); // the all window's own fresh value

        var scoped = WeeklyLabelled(view, "Fable wk");
        Assert.Equal(DisplayState.Dated, scoped.DisplayState);
        Assert.Equal(85m, scoped.Percent); // still the scoped window's own retained figure
    }
}
