using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// The notification engine (DESIGN.md §7 Notifications; tasks T36/T37), driven purely by a fake clock and
/// constructed <see cref="UsageView"/> sequences. Proves: a LIVE threshold crossing fires exactly once and
/// never re-fires until the window resets; escalation warn→crit fires again once; DATED / N-A readings
/// never fire; thresholds compare UNROUNDED; a provider transition needs a &gt;5-min dwell (a blip is
/// silent), fires once, is flap-suppressed for 30 min, and re-arms on recovery / a changed reason; a
/// benign opt-out ("disabled") and an always-down provider never toast.
/// </summary>
public sealed class NotificationDeciderTests
{
    private static readonly DisplayConfig Config = DisplayConfig.Default; // warn 80, crit 90
    private static readonly DateTimeOffset Base = new(2026, 07, 19, 14, 00, 00, TimeSpan.Zero);

    private const string Claude = "claude";
    private const string Codex = "codex";

    // ---- builders ----

    private static WindowView Live(string providerId, int minutes, decimal percent, DateTimeOffset resetsAt, DateTimeOffset observedAt)
        => new(
            providerId, minutes, WindowClassifier.Label(minutes), DisplayState.Live,
            percent, observedAt, Metric.Available(resetsAt, observedAt),
            UsageViewBuilder.SeverityFor(percent, Config), null);

    private static WindowView LiveKeyed(string providerId, int minutes, string windowKey, string label, decimal percent, DateTimeOffset resetsAt, DateTimeOffset observedAt)
        => new(
            providerId, minutes, label, DisplayState.Live,
            percent, observedAt, Metric.Available(resetsAt, observedAt),
            UsageViewBuilder.SeverityFor(percent, Config), null, windowKey);

    private static WindowView Dated(string providerId, int minutes, decimal percent, DateTimeOffset resetsAt, DateTimeOffset observedAt)
        => new(
            providerId, minutes, WindowClassifier.Label(minutes), DisplayState.Dated,
            percent, observedAt, Metric.Available(resetsAt, observedAt),
            percent >= Config.WarnPercent ? Severity.Warning : Severity.Normal, null);

    private static WindowView Na(string providerId, int minutes, string reason)
        => new(
            providerId, minutes, WindowClassifier.Label(minutes), DisplayState.NA,
            null, null, Metric.Unavailable<DateTimeOffset>(reason), Severity.Normal, reason);

    private static ProviderView Ok(string providerId, params WindowView[] windows)
        => Provider(providerId, SourceStatus.Ok, null, windows);

    private static ProviderView Unavailable(string providerId, string reason, params WindowView[] windows)
        => Provider(providerId, SourceStatus.Unavailable, reason, windows);

    private static ProviderView Provider(string providerId, SourceStatus status, string? reason, WindowView[] windows)
        => new(
            providerId, Base, status, reason, windows,
            Metric.NotApplicable<decimal>("not-reported"), Metric.NotApplicable<string>("not-reported"),
            Severity.Normal, Unknown: false, AllUnknown: false);

    private static UsageView View(params ProviderView[] providers)
        => new(Severity.Normal, Unknown: false, AllUnknown: false, providers);

    // ============================ Threshold (T37) ============================

    [Fact]
    public void Threshold_CrossingUpThroughWarn_FiresExactlyOnce_WithCountdownText()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = () => Base.AddHours(6).AddMinutes(12); // fixed reset boundary

        // First observation establishes a below-warn baseline: no crossing witnessed → silent.
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 50m, reset(), clock.GetUtcNow()))), clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(1));
        var fired = decider.Evaluate(
            View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 90m, reset(), clock.GetUtcNow()))),
            clock.GetUtcNow());

        var n = Assert.Single(fired);
        Assert.Equal(NotificationKind.Threshold, n.Kind);
        Assert.Equal(Severity.Critical, n.Severity); // 90 ≥ crit
        Assert.Equal("Claude Weekly at 90%", n.Title);
        Assert.Equal("Resets in 6h 11m.", n.Text); // 6h12m minus the 1-minute advance

        // Still at 90 next tick → no re-fire (no reset).
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(
            View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 90m, reset(), clock.GetUtcNow()))),
            clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_EscalatesWarnThenCrit_FiresOncePerBand()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        // baseline below warn
        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 50m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMinutes(1));
        var warn = Assert.Single(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 82m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
        Assert.Equal(Severity.Warning, warn.Severity);

        clock.Advance(TimeSpan.FromMinutes(1));
        var crit = Assert.Single(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 93m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
        Assert.Equal(Severity.Critical, crit.Severity);

        // holding at crit → nothing
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 93m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_JumpStraightToCritical_FiresOnceCritical_NotTwice()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 40m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMinutes(1));
        var fired = decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 96m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        var n = Assert.Single(fired);
        Assert.Equal(Severity.Critical, n.Severity);
    }

    [Fact]
    public void Threshold_DipWithoutReset_DoesNotRefire()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 50m, reset, clock.GetUtcNow()))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 85m, reset, clock.GetUtcNow()))), clock.GetUtcNow())); // warn

        // Dip back below warn (same window period — resets_at unchanged) then climb again: NO re-fire.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 60m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 85m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_WindowReset_ReArms_FiresAgain()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var period1 = Base.AddHours(2);
        var period2 = Base.AddDays(7); // a strictly later reset boundary = a new window period

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 50m, period1, clock.GetUtcNow()))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 85m, period1, clock.GetUtcNow()))), clock.GetUtcNow())); // warn

        // New period (resets_at advanced), fresh low reading → re-arm, silent baseline.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 15m, period2, clock.GetUtcNow()))), clock.GetUtcNow()));

        // Climb through warn again in the new period → fires once more.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 85m, period2, clock.GetUtcNow()))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_FirstObservationAlreadyAboveThreshold_DoesNotFire()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        // Already at 95% the very first time we see this window (e.g. app just started) — no witnessed
        // crossing, so no toast; and it stays quiet while held.
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 95m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 95m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_ComparesUnrounded_79Point9DoesNotFire_80Does()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 50m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        // 79.9 rounds to 80 for display but is UNROUNDED below the warn threshold → no crossing.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 79.9m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));

        // Exactly at the threshold → fires.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 80m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_DatedReading_NeverFires_EvenAtCritical()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        // A DATED monotone-floor reading at 95% must not toast (re-asserts T17/T18): it is history, not a
        // fresh LIVE number.
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Dated(Claude, WindowClassifier.WeeklyMinutes, 95m, reset, Base))), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Dated(Claude, WindowClassifier.WeeklyMinutes, 95m, reset, Base))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_NaReading_NeverFires()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        Assert.Empty(decider.Evaluate(View(Ok(Claude, Na(Claude, WindowClassifier.WeeklyMinutes, "no-recent-event"))), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Ok(Claude, Na(Claude, WindowClassifier.WeeklyMinutes, "reset-passed"))), clock.GetUtcNow()));
    }

    [Fact]
    public void Threshold_PerWindowArming_IsIndependent()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(3);

        // Baseline both the 5h and weekly windows below warn.
        decider.Evaluate(View(Ok(Claude,
            Live(Claude, WindowClassifier.FiveHourMinutes, 20m, Base.AddHours(3), clock.GetUtcNow()),
            Live(Claude, WindowClassifier.WeeklyMinutes, 50m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        // Only the weekly crosses — exactly one toast, and it is the weekly one.
        clock.Advance(TimeSpan.FromMinutes(1));
        var fired = decider.Evaluate(View(Ok(Claude,
            Live(Claude, WindowClassifier.FiveHourMinutes, 25m, Base.AddHours(3), clock.GetUtcNow()),
            Live(Claude, WindowClassifier.WeeklyMinutes, 85m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        var n = Assert.Single(fired);
        Assert.Contains("Weekly", n.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Threshold_TwoWeeklyWindows_SameMinutes_ArmIndependently_ByCompositeKey()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);
        var reset = Base.AddDays(4);
        const int weekly = 10080;

        // Two Claude weekly windows share WindowMinutes but have distinct composite keys. First tick: the
        // scoped window is ALREADY hot (85) while the all window is low (50). Neither toasts — each takes
        // its OWN silent baseline (review P0-1). With a shared minute-count key the scoped window would read
        // as a first-tick UP crossing of the merged arm and toast spuriously.
        Assert.Empty(decider.Evaluate(View(Ok(Claude,
            LiveKeyed(Claude, weekly, "10080", "Weekly", 50m, reset, clock.GetUtcNow()),
            LiveKeyed(Claude, weekly, "10080:Fable", "Fable wk", 85m, reset, clock.GetUtcNow()))), clock.GetUtcNow()));

        // Now weekly_all genuinely crosses warn. It MUST toast — a shared arm would already be "announced"
        // at Warning by the scoped window and suppress this real crossing.
        clock.Advance(TimeSpan.FromMinutes(1));
        var fired = decider.Evaluate(View(Ok(Claude,
            LiveKeyed(Claude, weekly, "10080", "Weekly", 82m, reset, clock.GetUtcNow()),
            LiveKeyed(Claude, weekly, "10080:Fable", "Fable wk", 85m, reset, clock.GetUtcNow()))), clock.GetUtcNow());

        var n = Assert.Single(fired);
        Assert.Equal("Claude Weekly at 82%", n.Title); // the all-models weekly, not the scoped one
    }

    // ============================ Transition (T36) ============================

    [Fact]
    public void Transition_FourMinuteBlip_IsSilent()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        Assert.Empty(decider.Evaluate(View(Ok(Codex, Live(Codex, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow())); // Ok

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Unavailable(Codex, "fetch-error")), clock.GetUtcNow())); // streak arms

        clock.Advance(TimeSpan.FromMinutes(3)); // now 4 min into the outage — still under the 5-min dwell
        Assert.Empty(decider.Evaluate(View(Unavailable(Codex, "fetch-error")), clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(1)); // recovers before the dwell elapses
        Assert.Empty(decider.Evaluate(View(Ok(Codex, Live(Codex, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), clock.GetUtcNow()))), clock.GetUtcNow()));
    }

    [Fact]
    public void Transition_SustainedBeyondDwell_FiresExactlyOnce()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "auth-rejected")), clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(6)); // 6 min into the outage → past the dwell
        var fired = decider.Evaluate(View(Unavailable(Claude, "auth-rejected")), clock.GetUtcNow());
        var n = Assert.Single(fired);
        Assert.Equal(NotificationKind.Transition, n.Kind);
        Assert.Equal(Severity.Warning, n.Severity);
        Assert.Equal("Claude unavailable", n.Title);
        Assert.Contains("auth rejected", n.Text, StringComparison.Ordinal);

        // Held down → no repeat (sustained = exactly one toast).
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "auth-rejected")), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "auth-rejected")), clock.GetUtcNow()));
    }

    [Fact]
    public void Transition_FlapSameReason_SuppressedWithin30Min_FiresAfter()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        // First outage fires at Base+7min (the flap anchor).
        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow())); // fire @ +7min

        // Recover, then re-fail with the SAME reason.
        clock.Advance(TimeSpan.FromMinutes(1)); // +8
        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), clock.GetUtcNow()))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1)); // +9
        decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow());

        // Dwell met on the new streak, but the same (provider, reason) fired 8 min ago → suppressed.
        clock.Advance(TimeSpan.FromMinutes(6)); // +15
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow()));

        // Past the 30-min flap window from the anchor (+7 → +38) → a repeat is allowed again.
        clock.Advance(TimeSpan.FromMinutes(23)); // +38
        Assert.Single(decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow()));
    }

    [Fact]
    public void Transition_ReasonChangesMidStreak_ReArmsForNewReason()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(decider.Evaluate(View(Unavailable(Claude, "throttled")), clock.GetUtcNow())); // throttled fires

        // The reason changes while still down → a different (provider, reason) is not suppressed → fires.
        clock.Advance(TimeSpan.FromMinutes(1));
        var fired = decider.Evaluate(View(Unavailable(Claude, "auth-rejected")), clock.GetUtcNow());
        var n = Assert.Single(fired);
        Assert.Contains("auth rejected", n.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Transition_RecoveryReArms_NewOutageFires()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(Unavailable(Claude, "timeout")), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(decider.Evaluate(View(Unavailable(Claude, "timeout")), clock.GetUtcNow())); // first fire

        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), clock.GetUtcNow()))), clock.GetUtcNow()); // recovery re-arms

        // A fresh outage with a DIFFERENT reason (sidesteps the 30-min flap guard) → fires again.
        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(Unavailable(Claude, "fetch-error")), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(decider.Evaluate(View(Unavailable(Claude, "fetch-error")), clock.GetUtcNow()));
    }

    [Fact]
    public void Transition_BenignDisabledReason_NeverFires()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        decider.Evaluate(View(Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(Unavailable(Claude, "disabled")), clock.GetUtcNow());

        // Even sustained well past the dwell, a deliberate opt-out is not an outage.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "disabled")), clock.GetUtcNow()));
    }

    [Fact]
    public void Transition_DownFromStart_NeverFires_NoPriorOk()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        // Unavailable from the very first observation (never seen Ok) → not a transition.
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "fetch-error")), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(decider.Evaluate(View(Unavailable(Claude, "fetch-error")), clock.GetUtcNow()));
    }

    [Fact]
    public void Transition_PerProviderIndependent()
    {
        var clock = new FakeTimeProvider(Base);
        var decider = new NotificationDecider(Config);

        // Both providers Ok, then only Claude goes down and stays down.
        decider.Evaluate(View(
            Ok(Claude, Live(Claude, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base)),
            Ok(Codex, Live(Codex, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), Base))), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMinutes(1));
        decider.Evaluate(View(
            Unavailable(Claude, "auth-rejected"),
            Ok(Codex, Live(Codex, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), clock.GetUtcNow()))), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMinutes(6));
        var fired = decider.Evaluate(View(
            Unavailable(Claude, "auth-rejected"),
            Ok(Codex, Live(Codex, WindowClassifier.WeeklyMinutes, 30m, Base.AddDays(3), clock.GetUtcNow()))), clock.GetUtcNow());

        var n = Assert.Single(fired);
        Assert.Equal("Claude unavailable", n.Title); // Codex, still Ok, contributes nothing
    }
}
