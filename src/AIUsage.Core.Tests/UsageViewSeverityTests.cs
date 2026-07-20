using Xunit;

using static AIUsage.Core.Tests.ViewTestData;

namespace AIUsage.Core.Tests;

/// <summary>
/// Severity, unknown-state propagation, and the monotone-floor (DESIGN.md §5, §7; tasks T11, T18).
/// Proves thresholds compare on UNROUNDED values, that an n/a region can never leave the icon reading
/// plain-safe, that everything-n/a is a distinct all-unknown state, and that a dated near-limit reading
/// drives Warning (capped, badged) — never a plain-safe icon and never "≥".
/// </summary>
public sealed class UsageViewSeverityTests
{
    private static readonly DisplayConfig Config = DisplayConfig.Default; // warn 80, crit 90

    // ---- SeverityFor: thresholds compared UNROUNDED ----

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(79.9, Severity.Normal)]
    [InlineData(80, Severity.Warning)]
    [InlineData(89.9, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    public void SeverityFor_MapsThresholds(decimal percent, Severity expected)
        => Assert.Equal(expected, UsageViewBuilder.SeverityFor(percent, Config));

    [Theory]
    [InlineData(79.5, Severity.Normal)]   // rounds to 80, but UNROUNDED < 80 → Normal
    [InlineData(89.5, Severity.Warning)]  // rounds to 90, but UNROUNDED < 90 → Warning
    public void SeverityFor_ComparesUnrounded_NotRoundedValue(decimal percent, Severity expected)
        => Assert.Equal(expected, UsageViewBuilder.SeverityFor(percent, Config));

    [Fact]
    public void LiveWindow_SeverityFlowsThroughBuild_Unrounded()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 89.5m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        Assert.Equal(Severity.Warning, view.Window(Codex, 10080).Severity); // 89.5 unrounded → Warning
        Assert.Equal(Severity.Warning, view.OverallSeverity);
    }

    // ---- Clean LIVE state: no unknown ----

    [Fact]
    public void AllLive_Safe_IsPlainNormal_NoUnknown()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 50m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        Assert.Equal(Severity.Normal, view.OverallSeverity);
        Assert.False(view.Unknown);
        Assert.False(view.AllUnknown);
    }

    [Fact]
    public void LiveCritical_DrivesCriticalOverall()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 95m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        Assert.Equal(Severity.Critical, view.OverallSeverity);
    }

    // ---- Unknown-state propagation (T11) ----

    [Fact]
    public void NaWindowAlongsideLiveSafe_ForcesOverallAwayFromPlainNormal()
    {
        // Weekly is LIVE and safe; the 5h window's reset has passed with no newer reading → n/a.
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base,
            Window(10080, 50m, Base, Base.AddHours(5)),   // live, safe
            Window(300, 60m, Base, Base.AddMinutes(-1))); // reset already passed → n/a
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        Assert.Equal(DisplayState.NA, view.Window(Codex, 300).DisplayState);
        Assert.Equal(Severity.Normal, view.OverallSeverity);
        Assert.True(view.Unknown);       // NOT plain-normal: the unknown badge is lit
        Assert.False(view.AllUnknown);   // weekly is still LIVE, so we can tell something
    }

    [Fact]
    public void EverythingNa_IsAllUnknown_ReadsAsCannotTell_NotSafe()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 73m, Base, Base.AddMinutes(-1))); // reset passed
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        Assert.Equal(DisplayState.NA, view.Window(Codex, 10080).DisplayState);
        Assert.Equal(Severity.Normal, view.OverallSeverity); // default, but...
        Assert.True(view.Unknown);
        Assert.True(view.AllUnknown); // ...the all-unknown flag stops the icon reading as safe
    }

    [Fact]
    public void UnavailableProviderWithNoWindows_IsAllUnknown()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = UnavailableSnapshot(Codex, Base, "no-sessions-dir");
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var provider = view.Providers.Single();

        Assert.Empty(provider.Windows);
        Assert.True(provider.AllUnknown);
        Assert.True(view.AllUnknown);
        Assert.True(view.Unknown);
        Assert.Equal(Severity.Normal, view.OverallSeverity);
    }

    [Fact]
    public void EmptySnapshots_IsAllUnknown()
    {
        var clock = new FakeTimeProvider(Base);
        var view = UsageViewBuilder.Build(Map(), new LastKnownReadingStore(), Config, clock);

        Assert.Empty(view.Providers);
        Assert.True(view.AllUnknown);
        Assert.True(view.Unknown);
    }

    [Fact]
    public void KnownSafeProvider_PlusUnknownProvider_IsWarnedByBadge_NotAllUnknown()
    {
        var clock = new FakeTimeProvider(Base);
        var codex = OkSnapshot(Codex, Base, Window(10080, 50m, Base, Base.AddHours(5))); // live, safe
        var claude = UnavailableSnapshot(Claude, Base, "auth-rejected");                  // unknown
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(codex, claude), store, Config, clock);

        Assert.Equal(Severity.Normal, view.OverallSeverity);
        Assert.True(view.Unknown);      // claude is unknown → not an unqualified safe icon
        Assert.False(view.AllUnknown);  // codex is live and safe → we can tell something
    }

    // ---- Deliberately-off providers are excluded from the unknown roll-up (T11 / review P2-11) ----

    [Theory]
    [InlineData("disabled")]
    [InlineData("paused")]
    public void BenignOffProvider_DoesNotLightUnknownBadge_WhenAnotherProviderIsLive(string benignReason)
    {
        // Claude deliberately OFF alongside a live-safe Codex: the icon must read plain-safe, NOT carry the
        // "?" unknown badge forever and train the owner to ignore it. The card still says "off" elsewhere.
        var clock = new FakeTimeProvider(Base);
        var codex = OkSnapshot(Codex, Base, Window(10080, 50m, Base, Base.AddHours(5))); // live, safe
        var claude = UnavailableSnapshot(Claude, Base, benignReason);
        var store = new LastKnownReadingStore();

        var view = UsageViewBuilder.Build(Map(codex, claude), store, Config, clock);

        Assert.Equal(Severity.Normal, view.OverallSeverity);
        Assert.False(view.Unknown);     // the off provider is excluded from the roll-up
        Assert.False(view.AllUnknown);
    }

    [Fact]
    public void NonBenignUnavailableProvider_StillLightsUnknownBadge()
    {
        // Contrast: an auth-rejected provider is a genuine outage, not an opt-out → the badge still lights.
        var clock = new FakeTimeProvider(Base);
        var codex = OkSnapshot(Codex, Base, Window(10080, 50m, Base, Base.AddHours(5)));
        var claude = UnavailableSnapshot(Claude, Base, "auth-rejected");

        var view = UsageViewBuilder.Build(Map(codex, claude), new LastKnownReadingStore(), Config, clock);

        Assert.True(view.Unknown);
        Assert.False(view.AllUnknown);
    }

    [Fact]
    public void AllProvidersOff_IsAllUnknown_CannotTell_NotSafe()
    {
        // Nothing is being monitored → the honest state is "cannot tell", never plain-safe.
        var clock = new FakeTimeProvider(Base);
        var claude = UnavailableSnapshot(Claude, Base, "disabled");

        var view = UsageViewBuilder.Build(Map(claude), new LastKnownReadingStore(), Config, clock);

        Assert.True(view.AllUnknown);
        Assert.True(view.Unknown);
    }

    // ---- Monotone-floor (T18) ----

    [Fact]
    public void DatedAtWarn_WindowUnreset_DrivesWarningPlusUnknown_ShownAsOfT_NotGtEq()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 85m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        clock.Advance(TimeSpan.FromMinutes(25)); // past TTL, before the 5h reset

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.Dated, window.DisplayState);
        Assert.Equal(Severity.Warning, window.Severity);
        Assert.Equal(85m, window.Percent);      // the honest "as of T" value, not a "≥" claim
        Assert.Equal(Base, window.ObservedAt);  // caption anchor
        Assert.Equal(Severity.Warning, view.OverallSeverity);
        Assert.True(view.Unknown);              // always paired with the unknown badge
        Assert.False(view.AllUnknown);          // the floor IS a real signal
    }

    [Fact]
    public void DatedAtCritical_IsCappedAtWarning_NeverCritical()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 95m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        clock.Advance(TimeSpan.FromMinutes(25));

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);

        // A dated near-limit is a floor, not a measurement — it must not assert Critical certainty.
        Assert.Equal(Severity.Warning, view.Window(Codex, 10080).Severity);
        Assert.Equal(Severity.Warning, view.OverallSeverity);
    }

    [Fact]
    public void DatedBelowWarn_DrivesNoSeverity_IsAllUnknown()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 50m, Base, Base.AddHours(5)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        clock.Advance(TimeSpan.FromMinutes(25)); // past TTL, before reset

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        Assert.Equal(DisplayState.Dated, window.DisplayState);
        Assert.Equal(Severity.Normal, window.Severity);
        Assert.Equal(Severity.Normal, view.OverallSeverity);
        Assert.True(view.Unknown);
        Assert.True(view.AllUnknown); // a below-warn dated value is not a severity signal → cannot tell
    }

    [Fact]
    public void MonotoneFloor_AfterReset_ResumesNormalUnknownHandling()
    {
        var clock = new FakeTimeProvider(Base);
        var snapshot = OkSnapshot(Codex, Base, Window(10080, 85m, Base, Base.AddMinutes(30)));
        var store = new LastKnownReadingStore();
        store.RecordFrom(snapshot);

        clock.Advance(TimeSpan.FromMinutes(31)); // past BOTH the TTL and the reset boundary

        var view = UsageViewBuilder.Build(Map(snapshot), store, Config, clock);
        var window = view.Window(Codex, 10080);

        // The floor no longer holds once the window resets: n/a, not Warning.
        Assert.Equal(DisplayState.NA, window.DisplayState);
        Assert.Equal("reset-passed", window.ReasonCode);
        Assert.Equal(Severity.Normal, view.OverallSeverity);
        Assert.True(view.AllUnknown);
    }
}
