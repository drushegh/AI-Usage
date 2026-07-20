using Xunit;

using static AIUsage.Core.Tests.ViewTestData;

namespace AIUsage.Core.Tests;

/// <summary>
/// The per-provider LIVE freshness TTL (DESIGN.md §5 LIVE rule 2; task T30): Claude is LIVE only within
/// 210s of the observation, while Codex keeps its 20-minute TTL. A KNOWN failed refresh already yields
/// Unavailable from the provider (immediate invalidation); this TTL only bounds a stale-but-Ok reading.
/// Every decision is driven by the fake clock, and the existing Codex freshness tests stay green.
/// </summary>
public sealed class UsageViewClaudeTtlTests
{
    private static readonly DisplayConfig Config = DisplayConfig.Default; // 20-min Codex, 210s Claude

    [Fact]
    public void Default_ClaudeCurrentTtl_Is210Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(210), DisplayConfig.Default.ClaudeCurrentTtl);

    [Fact]
    public void TtlFor_IsClaudeTtlForClaude_CodexTtlForEveryoneElse()
    {
        Assert.Equal(Config.ClaudeCurrentTtl, UsageViewBuilder.TtlFor(Claude, Config));
        Assert.Equal(Config.CodexCurrentTtl, UsageViewBuilder.TtlFor(Codex, Config));
    }

    [Fact]
    public void ClaudeWindow_Within210s_IsLive()
    {
        var clock = new FakeTimeProvider(Base);
        var claude = OkSnapshot(Claude, Base, Window(10080, 42m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();

        clock.Advance(TimeSpan.FromSeconds(200)); // < 210s

        var view = UsageViewBuilder.Build(Map(claude), store, Config, clock);
        Assert.Equal(DisplayState.Live, view.Window(Claude, 10080).DisplayState);
    }

    [Fact]
    public void ClaudeWindow_Past210s_IsNotLive_ButCodexAtSameAgeStillIs()
    {
        var clock = new FakeTimeProvider(Base);
        var claude = OkSnapshot(Claude, Base, Window(10080, 42m, Base, Base.AddHours(5)));
        var codex = OkSnapshot(Codex, Base, Window(10080, 42m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(claude);
        store.RecordFrom(codex);

        clock.Advance(TimeSpan.FromSeconds(240)); // past Claude's 210s, well inside Codex's 20-min TTL

        var view = UsageViewBuilder.Build(Map(claude, codex), store, Config, clock);

        // Claude expired to DATED (its window is unreset and history was recorded).
        Assert.Equal(DisplayState.Dated, view.Window(Claude, 10080).DisplayState);
        // Codex at the very same age is still LIVE — proving the TTL is applied PER PROVIDER.
        Assert.Equal(DisplayState.Live, view.Window(Codex, 10080).DisplayState);
    }

    [Fact]
    public void ClaudeWindow_Past210s_NoHistory_IsNa_NoRecentEvent()
    {
        var clock = new FakeTimeProvider(Base);
        var claude = OkSnapshot(Claude, Base, Window(10080, 42m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore(); // no RecordFrom → no DATED fallback

        clock.Advance(TimeSpan.FromSeconds(240));

        var view = UsageViewBuilder.Build(Map(claude), store, Config, clock);
        var window = view.Window(Claude, 10080);

        Assert.Equal(DisplayState.NA, window.DisplayState);
        Assert.Equal("no-recent-event", window.ReasonCode);
        Assert.Null(window.Percent);
    }
}
