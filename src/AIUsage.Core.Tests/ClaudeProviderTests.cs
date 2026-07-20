using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// The Claude provider (DESIGN.md §4.2; tasks T27/T28/T29/T30/T32). Every test drives a FAKE clock, a
/// FAKE probe runner, and a FAKE version source — never the real exe, never the network. Proves the
/// opt-in kill switch, the no-version guard, the exit-code → honest-n/a map, the 180s self-gate +
/// coalescing, the backoff ladder, and the 5×429 → diagnostic-state flip.
/// </summary>
public sealed class ClaudeProviderTests
{
    private static readonly DateTimeOffset Base = new(2026, 07, 20, 12, 00, 00, TimeSpan.Zero);
    private const string Version = "2.1.191";

    private static FakeClaudeVersionSource VersionOk() =>
        new(new ClaudeVersionResult(Version, ClaudeVersionSource.PackageJson));

    private static ClaudeProbeResult Ok(string body) => new(ClaudeProbeExitCodes.Ok, body);

    private static ClaudeProbeResult Fail(int exitCode) => new(exitCode, null);

    // ---- Opt-in / kill switch (T32) --------------------------------------------------------------

    [Fact]
    public async Task Disabled_IsUnavailableDisabled_NoProbe_NoVersionResolve()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var version = VersionOk();
        var provider = new ClaudeProvider(probe, version, clock, enabled: false, new FakeClaudeGateStore());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("disabled", snapshot.StatusReasonCode);
        Assert.Equal(0, probe.Calls);     // never spawned
        Assert.Equal(0, version.Calls);   // never resolved
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task KillSwitch_TogglesAtRuntime()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        Assert.Equal(SourceStatus.Ok, (await provider.FetchAsync(CancellationToken.None)).Status);
        Assert.Equal(1, probe.Calls);

        // Flip OFF at runtime → disabled, no further spawn even after the gate would have re-opened.
        provider.Enabled = false;
        clock.Advance(TimeSpan.FromSeconds(300));
        var off = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal("disabled", off.StatusReasonCode);
        Assert.Equal(1, probe.Calls);

        // Flip back ON → the (now-elapsed) gate lets it fetch again.
        provider.Enabled = true;
        var on = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(SourceStatus.Ok, on.Status);
        Assert.Equal(2, probe.Calls);
    }

    // ---- No version (T23 wiring) -----------------------------------------------------------------

    [Fact]
    public async Task NoVersion_IsUnavailableNoVersion_NoProbe()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var version = new FakeClaudeVersionSource(null); // never resolved on this machine
        var provider = new ClaudeProvider(probe, version, clock, enabled: true, new FakeClaudeGateStore());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-version", snapshot.StatusReasonCode);
        Assert.Equal(0, probe.Calls);   // never fabricate a version, never spawn
        Assert.Equal(1, version.Calls);
    }

    // ---- Exit 0 + the E1 fixture → Ok with the expected windows; backoff reset --------------------

    [Fact]
    public async Task Ok_WithE1Fixture_ProducesExpectedWindows_AndResetsBackoffToBase()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        Assert.Null(snapshot.StatusReasonCode);
        Assert.Equal(Version, probe.LastVersion); // the resolved version reached the probe argv

        // 5h 28% · Weekly 37% · Fable wk 43% (the E1 capture).
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal(28.0m, WindowByLabel(snapshot, "5h").UsedPercent.Value);
        Assert.Equal(37.0m, WindowByLabel(snapshot, "Weekly").UsedPercent.Value);
        Assert.Equal(43m, WindowByLabel(snapshot, "Fable wk").UsedPercent.Value);

        // Success resets the gate to the 180s base cadence.
        Assert.Equal(TimeSpan.FromSeconds(180), provider.NextEligibleAt - Base);
    }

    [Fact]
    public async Task Ok_WithDriftBody_IsSourceChanged_AndBacksOffToCap()
    {
        var clock = new FakeTimeProvider(Base);
        // A 200 whose body is JSON but not a usable object → the parser reports drift.
        var provider = new ClaudeProvider(new FakeClaudeProbeRunner(Ok("[]")), VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("source-changed", snapshot.StatusReasonCode);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(TimeSpan.FromMinutes(60), provider.NextEligibleAt - Base); // straight to the cap
    }

    // ---- Exit-code → outcome map -----------------------------------------------------------------

    [Theory]
    [InlineData(ClaudeProbeExitCodes.AuthRejected, "auth-rejected")]
    [InlineData(ClaudeProbeExitCodes.Throttled, "throttled")]
    [InlineData(ClaudeProbeExitCodes.Transport, "fetch-error")]
    [InlineData(ClaudeProbeExitCodes.Schema, "source-changed")]
    [InlineData(ClaudeProbeExitCodes.Credentials, "no-credentials")]
    public async Task ExitCode_MapsToHonestReason(int exitCode, string expectedReason)
    {
        var clock = new FakeTimeProvider(Base);
        var provider = new ClaudeProvider(new FakeClaudeProbeRunner(Fail(exitCode)), VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal(expectedReason, snapshot.StatusReasonCode);
        Assert.Empty(snapshot.Windows);
        // The whole snapshot degrades to n/a with the reason — never a fabricated zero.
        Assert.Equal(MetricState.Unavailable, snapshot.CreditsBalance.State);
        Assert.Equal(expectedReason, snapshot.CreditsBalance.ReasonCode);
        Assert.Equal(MetricState.Unavailable, snapshot.PlanType.State);
    }

    // ---- 180s self-gate + coalescing (T27) -------------------------------------------------------

    [Fact]
    public async Task WithinGate_MultipleFetches_SpawnProbeOnce_AndReturnLastSnapshot()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var first = await provider.FetchAsync(CancellationToken.None);   // spawns
        var second = await provider.FetchAsync(CancellationToken.None);  // gated (now < Base+180)
        clock.Advance(TimeSpan.FromSeconds(179));
        var third = await provider.FetchAsync(CancellationToken.None);   // still gated

        Assert.Equal(1, probe.Calls);       // manual refresh + restart + resume all coalesce
        Assert.Same(first, second);          // the last published snapshot, unchanged
        Assert.Same(first, third);

        // At the gate boundary it spawns again.
        clock.Advance(TimeSpan.FromSeconds(1)); // now = Base + 180
        var fourth = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, probe.Calls);
        Assert.NotSame(first, fourth);
    }

    // ---- Backoff ladder (T27): 3 → 6 → 12 → 24 → 48 → 60 (cap); success → 180s -------------------

    [Fact]
    public async Task ConsecutiveFailures_WalkTheBackoffLadder_CappedAt60()
    {
        var clock = new FakeTimeProvider(Base);
        var provider = new ClaudeProvider(new FakeClaudeProbeRunner(Fail(ClaudeProbeExitCodes.Transport)), VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        int[] expectedMinutes = { 3, 6, 12, 24, 48, 60, 60 }; // walks, then saturates at the cap
        foreach (var minutes in expectedMinutes)
        {
            AdvanceToEligible(provider, clock);
            var before = clock.GetUtcNow();
            await provider.FetchAsync(CancellationToken.None);
            Assert.Equal(TimeSpan.FromMinutes(minutes), provider.NextEligibleAt - before);
        }
    }

    [Fact]
    public async Task SuccessAfterFailures_ResetsBackoffToBase180s()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(
            Fail(ClaudeProbeExitCodes.Transport),
            Fail(ClaudeProbeExitCodes.Transport),
            Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        // fail → 3m
        var before1 = clock.GetUtcNow();
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(3), provider.NextEligibleAt - before1);

        // fail → 6m
        AdvanceToEligible(provider, clock);
        var before2 = clock.GetUtcNow();
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(6), provider.NextEligibleAt - before2);

        // success → reset to the 180s base
        AdvanceToEligible(provider, clock);
        var before3 = clock.GetUtcNow();
        var success = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(SourceStatus.Ok, success.Status);
        Assert.Equal(TimeSpan.FromSeconds(180), provider.NextEligibleAt - before3);

        // A follow-up within the base gate is coalesced (no new spawn).
        var callsAfterSuccess = probe.Calls;
        clock.Advance(TimeSpan.FromSeconds(179));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(callsAfterSuccess, probe.Calls);
    }

    // ---- 5×429 → endpoint-or-UA-changed (T28) ----------------------------------------------------

    [Fact]
    public async Task FiveConsecutive429s_FlipToEndpointOrUaChanged_AtTheCap()
    {
        var clock = new FakeTimeProvider(Base);
        var provider = new ClaudeProvider(new FakeClaudeProbeRunner(Fail(ClaudeProbeExitCodes.Throttled)), VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var reasons = new List<string?>();
        TimeSpan lastDelta = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            AdvanceToEligible(provider, clock);
            var before = clock.GetUtcNow();
            var snapshot = await provider.FetchAsync(CancellationToken.None);
            reasons.Add(snapshot.StatusReasonCode);
            lastDelta = provider.NextEligibleAt - before;
        }

        // The first four 429s read "throttled"; the fifth stops the lie and flips to the diagnostic state.
        Assert.Equal(new[] { "throttled", "throttled", "throttled", "throttled", "endpoint-or-UA-changed" }, reasons);
        Assert.Equal(TimeSpan.FromMinutes(60), lastDelta); // held at the 60-min cap
    }

    [Fact]
    public async Task NonThrottleFailure_ResetsThe429Counter()
    {
        var clock = new FakeTimeProvider(Base);
        // 429, 429, then a transport failure resets the counter, then 429s stay "throttled" (never reach 5 in a row).
        var probe = new FakeClaudeProbeRunner(
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Transport),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        string? last = null;
        for (var i = 0; i < 7; i++)
        {
            AdvanceToEligible(provider, clock);
            last = (await provider.FetchAsync(CancellationToken.None)).StatusReasonCode;
        }

        // Without the reset, the 4 trailing 429s would total 6 consecutive and trip the diagnostic — the
        // interposed transport failure clears the counter, so the run stays "throttled".
        Assert.Equal("throttled", last);
    }

    // ---- P1-2: the cadence gate persists across restarts -----------------------------------------

    [Fact]
    public async Task GateAdvance_IsPersisted_ToTheGateStore()
    {
        var clock = new FakeTimeProvider(Base);
        var gate = new FakeClaudeGateStore();
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, gate);

        await provider.FetchAsync(CancellationToken.None);

        Assert.True(gate.Saves > 0);                       // the advancing gate was persisted
        Assert.NotNull(gate.Saved);
        Assert.Equal(Base + TimeSpan.FromSeconds(180), gate.Saved!.NextEligibleAt); // success → 180s base
        Assert.Equal(-1, gate.Saved.BackoffIndex);
    }

    [Fact]
    public async Task PersistedClosedGate_IsHonouredAfterRestart_NoImmediateProbe()
    {
        // A "restart": a fresh provider loads a persisted gate that is still 20 min out.
        var clock = new FakeTimeProvider(Base);
        var gate = new FakeClaudeGateStore(new ClaudeGateState(Base + TimeSpan.FromMinutes(20), BackoffIndex: 1, ConsecutiveThrottle: 0));
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, gate);

        // The restart must NOT fire an immediate real request — the persisted gate coalesces it.
        var gated = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(SourceStatus.Unavailable, gated.Status);
        Assert.Equal(Base + TimeSpan.FromMinutes(20), provider.NextEligibleAt);

        // Once the persisted gate elapses, it fetches for real.
        clock.Advance(TimeSpan.FromMinutes(20));
        var live = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(SourceStatus.Ok, live.Status);
    }

    [Fact]
    public void PersistedGate_IsClampedTo60MinutesOnLoad()
    {
        // A corrupt / far-future persisted value cannot wedge the provider past the 60-min cap.
        var clock = new FakeTimeProvider(Base);
        var gate = new FakeClaudeGateStore(new ClaudeGateState(Base + TimeSpan.FromHours(5), BackoffIndex: 5, ConsecutiveThrottle: 9));
        var provider = new ClaudeProvider(new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture())), VersionOk(), clock, enabled: true, gate);

        Assert.Equal(Base + TimeSpan.FromMinutes(60), provider.NextEligibleAt);
    }

    // ---- sol P1: the cadence reservation is cancellation-safe -------------------------------------

    [Fact]
    public async Task CancelledAttempt_RetainsCadenceReservation_NextTriggerIsGated()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new BlockingClaudeProbeRunner(honorCancellation: true);
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        using var cts = new CancellationTokenSource();
        var attempt = provider.FetchAsync(cts.Token); // launches, reserves +180s, then blocks in the probe
        await probe.Started;

        // A genuine shutdown cancels the caller's token; the probe may already have sent, so the reservation
        // set BEFORE launch must stand.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt);

        // The reservation is retained: the next trigger is coalesced, NOT an immediate second real request.
        var gated = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(SourceStatus.Unavailable, gated.Status);
        Assert.Equal(Base + TimeSpan.FromSeconds(180), provider.NextEligibleAt);
    }

    // ---- sol P1: the gate is a MONOTONIC floor, immune to wall-clock jumps ------------------------

    [Fact]
    public async Task ForwardWallClockJump_DoesNotOpenTheGateEarly()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        await provider.FetchAsync(CancellationToken.None); // Calls==1; monotonic floor 180s out
        Assert.Equal(1, probe.Calls);

        // The system clock jumps an hour forward, but no real time has elapsed — the monotonic floor holds.
        clock.JumpWallClock(TimeSpan.FromHours(1));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, probe.Calls); // did NOT fire early despite the wall clock being way past

        // Real time actually passes → the gate opens on the true 180s cadence.
        clock.Advance(TimeSpan.FromSeconds(180));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task BackwardWallClockJump_DoesNotFireTheGateEarly()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        await provider.FetchAsync(CancellationToken.None); // Calls==1; monotonic floor 180s out
        clock.Advance(TimeSpan.FromSeconds(90));           // 90 real seconds elapse

        // The wall clock is wound back an hour; the monotonic floor must ignore it (only 90s really passed).
        clock.JumpWallClock(TimeSpan.FromHours(-1));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, probe.Calls); // still gated — the rewind cannot open it early

        // The remaining real time elapses → the gate opens exactly on the true cadence.
        clock.Advance(TimeSpan.FromSeconds(90));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, probe.Calls);
    }

    // ---- sol P1: disabling cancels an in-flight attempt ------------------------------------------

    [Fact]
    public async Task Disable_DuringInFlight_CancelsTheAttempt_PublishesDisabled()
    {
        var clock = new FakeTimeProvider(Base);
        var probe = new BlockingClaudeProbeRunner(honorCancellation: true);
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var attempt = provider.FetchAsync(CancellationToken.None);
        await probe.Started;

        provider.Enabled = false; // must cancel the running probe

        var snapshot = await attempt;
        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("disabled", snapshot.StatusReasonCode);
    }

    [Fact]
    public async Task Disable_DuringInFlight_DiscardsLateOk_ByEnableGeneration()
    {
        var clock = new FakeTimeProvider(Base);
        // The probe IGNORES cancellation and returns Ok AFTER the disable — the generation guard must
        // discard it so a late success can never publish over the disabled snapshot.
        var probe = new BlockingClaudeProbeRunner(honorCancellation: false);
        var provider = new ClaudeProvider(probe, VersionOk(), clock, enabled: true, new FakeClaudeGateStore());

        var attempt = provider.FetchAsync(CancellationToken.None);
        await probe.Started;

        provider.Enabled = false;
        probe.Complete(Ok(ClaudeTestData.ReadFixture())); // a late success

        var snapshot = await attempt;
        Assert.NotEqual(SourceStatus.Ok, snapshot.Status);
        Assert.Equal("disabled", snapshot.StatusReasonCode);
    }

    // ---- P1-3: the Claude Code version self-heals -------------------------------------------------

    [Fact]
    public async Task EndpointOrUaChanged_ClearsCachedVersion_ReResolvesWithNewUa()
    {
        var clock = new FakeTimeProvider(Base);
        var version = new FakeClaudeVersionSource(new ClaudeVersionResult("2.1.191", ClaudeVersionSource.PackageJson));
        // Five 429s trip the diagnostic state; the sixth attempt (after the fix ships) succeeds.
        var probe = new FakeClaudeProbeRunner(
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Fail(ClaudeProbeExitCodes.Throttled),
            Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, version, clock, enabled: true, new FakeClaudeGateStore());

        for (var i = 0; i < 5; i++)
        {
            AdvanceToEligible(provider, clock);
            await provider.FetchAsync(CancellationToken.None);
        }

        // The version resolved exactly once during the storm (cached thereafter); the diagnostic flip
        // cleared that cache.
        Assert.Equal(1, version.Calls);
        Assert.Equal("2.1.191", probe.LastVersion);

        // The fix lands on disk: a newer Claude Code version.
        version.SetResult(new ClaudeVersionResult("2.2.0", ClaudeVersionSource.PackageJson));

        // The next eligible attempt re-resolves (cache was cleared) and sends the NEW User-Agent → self-heal.
        AdvanceToEligible(provider, clock);
        var healed = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, version.Calls);
        Assert.Equal("2.2.0", probe.LastVersion);
        Assert.Equal(SourceStatus.Ok, healed.Status);
    }

    [Fact]
    public async Task Version_IsReResolvedDaily_PicksUpAChangedVersion()
    {
        var clock = new FakeTimeProvider(Base);
        var version = new FakeClaudeVersionSource(new ClaudeVersionResult("2.1.191", ClaudeVersionSource.PackageJson));
        var probe = new FakeClaudeProbeRunner(Ok(ClaudeTestData.ReadFixture()));
        var provider = new ClaudeProvider(probe, version, clock, enabled: true, new FakeClaudeGateStore());

        await provider.FetchAsync(CancellationToken.None); // resolves once
        Assert.Equal(1, version.Calls);

        // A fetch a few minutes later reuses the cached version (no re-resolve).
        clock.Advance(TimeSpan.FromMinutes(10));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, version.Calls);

        // More than a day later → an opportunistic re-resolve picks up the changed version.
        version.SetResult(new ClaudeVersionResult("2.2.0", ClaudeVersionSource.PackageJson));
        clock.Advance(TimeSpan.FromHours(25));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, version.Calls);
        Assert.Equal("2.2.0", probe.LastVersion);
    }

    private static UsageWindow WindowByLabel(ProviderSnapshot snapshot, string label) =>
        snapshot.Windows.Single(w => w.Label == label);

    private static void AdvanceToEligible(ClaudeProvider provider, FakeTimeProvider clock)
    {
        var wait = provider.NextEligibleAt - clock.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            clock.Advance(wait);
        }
    }
}
