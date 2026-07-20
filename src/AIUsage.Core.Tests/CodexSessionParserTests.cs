using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Acceptance tests for the pure Codex parser, driven by the four DEFINITIVE E3 fixtures plus
/// synthetic lines for the selection/edge axes (spike <c>e3-findings.md</c>). This is the highest-risk
/// unit — it parses the real file format — so every fixture and every "6-step" edge case is pinned here.
/// </summary>
public sealed class CodexSessionParserTests
{
    private static readonly DateTimeOffset FetchedAt =
        new(2026, 07, 19, 12, 00, 00, TimeSpan.Zero);

    private static UsageWindow WindowOfKind(ProviderSnapshot snapshot, WindowKind kind)
        => Assert.Single(snapshot.Windows, w => WindowClassifier.Classify(w.WindowMinutes) == kind);

    // ---- Fixture: weekly-only (current schema) --------------------------------------------------

    [Fact]
    public void WeeklyOnlyFixture_ProducesSingleWeeklyWindow_PlanAndCredits()
    {
        var snapshot = CodexSessionParser.BuildSnapshot(
            new[] { CodexTestData.ReadFixture("fixture-weekly-only.jsonl") }, FetchedAt);

        Assert.Equal(CodexProvider.ProviderId, snapshot.ProviderId);
        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        Assert.Null(snapshot.StatusReasonCode);
        Assert.Equal(FetchedAt, snapshot.FetchedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(WindowKind.Weekly, WindowClassifier.Classify(window.WindowMinutes));
        Assert.Equal(10080, window.WindowMinutes);
        Assert.Equal(MetricState.Available, window.UsedPercent.State);
        Assert.Equal(4.0m, window.UsedPercent.Value);

        // Observed at the NEWEST line's embedded UTC timestamp (10:09:25.376Z), not the earlier line.
        Assert.Equal(new DateTimeOffset(2026, 07, 19, 10, 09, 25, 376, TimeSpan.Zero), window.UsedPercent.ObservedAt);

        Assert.Equal(MetricState.Available, window.ResetsAt.State);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785016789), window.ResetsAt.Value);

        // The 5h window is genuinely gone on current accounts — absent, never a fabricated zero row.
        Assert.DoesNotContain(snapshot.Windows, w => WindowClassifier.Classify(w.WindowMinutes) == WindowKind.FiveHour);

        Assert.Equal(MetricState.Available, snapshot.PlanType.State);
        Assert.Equal("plus", snapshot.PlanType.Value);
        Assert.Equal(MetricState.Available, snapshot.CreditsBalance.State);
        Assert.Equal(0m, snapshot.CreditsBalance.Value); // "0E-10" parses to 0
    }

    // ---- Fixture: legacy both windows (classify by window_minutes, not position) ----------------

    [Fact]
    public void LegacyBothWindowsFixture_ClassifiesByWindowMinutes_NotByPrimarySecondaryPosition()
    {
        // Newest line is the 2025-12 era: primary=300 (5h) used 12%, secondary=10080 (weekly) used 4%.
        var snapshot = CodexSessionParser.BuildSnapshot(
            new[] { CodexTestData.ReadFixture("fixture-legacy-both-windows.jsonl") }, FetchedAt);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Windows.Count);

        var fiveHour = WindowOfKind(snapshot, WindowKind.FiveHour);
        Assert.Equal(300, fiveHour.WindowMinutes);
        Assert.Equal(12.0m, fiveHour.UsedPercent.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1765597845), fiveHour.ResetsAt.Value);

        var weekly = WindowOfKind(snapshot, WindowKind.Weekly);
        Assert.Equal(10080, weekly.WindowMinutes);
        Assert.Equal(4.0m, weekly.UsedPercent.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1766184645), weekly.ResetsAt.Value);

        // Deterministic display order is ascending by WindowMinutes (5h then weekly).
        Assert.Equal(300, snapshot.Windows[0].WindowMinutes);
        Assert.Equal(10080, snapshot.Windows[1].WindowMinutes);

        // The 2025-12 line chosen over the older 2025-10 line — proven by the observation timestamp.
        Assert.Equal(new DateTimeOffset(2025, 12, 13, 00, 08, 26, 171, TimeSpan.Zero), fiveHour.UsedPercent.ObservedAt);

        // plan_type:null and credits.balance:null on this line → each independently NotApplicable.
        Assert.Equal(MetricState.NotApplicable, snapshot.PlanType.State);
        Assert.Equal(MetricState.NotApplicable, snapshot.CreditsBalance.State);
    }

    [Fact]
    public void LegacyOldestLine_RelativeReset_IsComputedFromEventTimestamp()
    {
        // The oldest era encodes reset as relative `resets_in_seconds` — resolve it against the event's
        // embedded UTC timestamp. Isolate line 1 (older than line 2, so BuildSnapshot would otherwise pick line 2).
        var line0 = CodexTestData.ReadFixture("fixture-legacy-both-windows.jsonl").Split('\n')[0];
        var snapshot = CodexSessionParser.BuildSnapshot(new[] { line0 }, FetchedAt);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        var eventTimestamp = new DateTimeOffset(2025, 10, 18, 00, 31, 39, 914, TimeSpan.Zero);

        var fiveHour = WindowOfKind(snapshot, WindowKind.FiveHour);
        Assert.Equal(299, fiveHour.WindowMinutes);
        Assert.Equal(7.0m, fiveHour.UsedPercent.Value);
        Assert.Equal(eventTimestamp.AddSeconds(14010), fiveHour.ResetsAt.Value);

        var weekly = WindowOfKind(snapshot, WindowKind.Weekly);
        Assert.Equal(10079, weekly.WindowMinutes);
        Assert.Equal(2.0m, weekly.UsedPercent.Value);
        Assert.Equal(eventTimestamp.AddSeconds(600810), weekly.ResetsAt.Value);

        // Oldest schema omits plan_type and credits entirely.
        Assert.Equal(MetricState.NotApplicable, snapshot.PlanType.State);
        Assert.Equal(MetricState.NotApplicable, snapshot.CreditsBalance.State);
    }

    // ---- Fixture: degenerate tail (skip trailing null-primary, pick newest non-null) ------------

    [Fact]
    public void DegenerateTailFixture_SkipsNullPrimary_PicksNewestNonNullEvent()
    {
        var snapshot = CodexSessionParser.BuildSnapshot(
            new[] { CodexTestData.ReadFixture("fixture-degenerate-tail.jsonl") }, FetchedAt);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);

        var fiveHour = WindowOfKind(snapshot, WindowKind.FiveHour);
        // The LATER line (17:19:16) has primary:null and must be skipped; the 17:12:02 line is chosen.
        Assert.Equal(new DateTimeOffset(2026, 04, 30, 17, 12, 02, 103, TimeSpan.Zero), fiveHour.UsedPercent.ObservedAt);
        Assert.Equal(1.0m, fiveHour.UsedPercent.Value);

        var weekly = WindowOfKind(snapshot, WindowKind.Weekly);
        Assert.Equal(MetricState.Available, weekly.UsedPercent.State);
        Assert.Equal(0.0m, weekly.UsedPercent.Value); // a genuine observed 0% is Available, not n/a

        Assert.Equal("team", snapshot.PlanType.Value);
        Assert.Equal(MetricState.NotApplicable, snapshot.CreditsBalance.State); // credits:null on the chosen line
    }

    // ---- Fixture: partial line (tolerate leading fragment + truncated final line) ---------------

    [Fact]
    public void PartialLineFixture_ToleratesFragments_RecoversTheGoodMiddleLine()
    {
        var snapshot = CodexSessionParser.BuildSnapshot(
            new[] { CodexTestData.ReadFixture("fixture-partial-line.jsonl") }, FetchedAt);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(WindowKind.Weekly, WindowClassifier.Classify(window.WindowMinutes));
        Assert.Equal(4.0m, window.UsedPercent.Value);
        // The only fully-valid line is the middle one (00:40:19.457Z); fragment + truncated tail skipped.
        Assert.Equal(new DateTimeOffset(2026, 07, 19, 00, 40, 19, 457, TimeSpan.Zero), window.UsedPercent.ObservedAt);

        Assert.Equal("plus", snapshot.PlanType.Value);
        Assert.Equal(0m, snapshot.CreditsBalance.Value);
    }

    // ---- Newest-by-embedded-timestamp selection across candidates -------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewestByEmbeddedTimestamp_WinsRegardlessOfCandidateOrder(bool reversed)
    {
        var baseTime = new DateTimeOffset(2026, 07, 19, 09, 00, 00, TimeSpan.Zero);
        var older = CodexTestData.MakeLine(baseTime, primaryUsed: 10m);
        var newer = CodexTestData.MakeLine(baseTime.AddMinutes(10), primaryUsed: 90m);

        var tails = reversed ? new[] { newer, older } : new[] { older, newer };
        var snapshot = CodexSessionParser.BuildSnapshot(tails, FetchedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(90m, window.UsedPercent.Value); // the newer embedded timestamp wins, not list order
        Assert.Equal(baseTime.AddMinutes(10), window.UsedPercent.ObservedAt);
    }

    [Fact]
    public void Selection_IsByTimestamp_NotByUsedPercentMagnitudeOrOrder()
    {
        var baseTime = new DateTimeOffset(2026, 07, 19, 09, 00, 00, TimeSpan.Zero);
        // Candidate listed FIRST has the higher used% but an OLDER timestamp; the newer, lower-% event wins.
        var earlierHigh = CodexTestData.MakeLine(baseTime.AddMinutes(10), primaryUsed: 90m);
        var laterLow = CodexTestData.MakeLine(baseTime.AddMinutes(20), primaryUsed: 30m);

        var snapshot = CodexSessionParser.BuildSnapshot(new[] { earlierHigh, laterLow }, FetchedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(30m, window.UsedPercent.Value);
        Assert.Equal(baseTime.AddMinutes(20), window.UsedPercent.ObservedAt);
    }

    // ---- Future-dated observations are quarantined (review: future observations) ----------------

    [Fact]
    public void FutureDatedEvent_IsQuarantined_DoesNotEclipseValidPastEvents()
    {
        // A malformed FUTURE event (10 min past the fetch time) with a high percent must NOT be selected
        // over the valid, older event — otherwise it eclipses reality and sits LIVE forever.
        var valid = CodexTestData.MakeLine(FetchedAt.AddMinutes(-5), primaryUsed: 30m);
        var future = CodexTestData.MakeLine(FetchedAt.AddMinutes(10), primaryUsed: 90m);

        var snapshot = CodexSessionParser.BuildSnapshot(new[] { valid, future }, FetchedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(30m, window.UsedPercent.Value); // the valid past reading, not the future 90%
        Assert.Equal(FetchedAt.AddMinutes(-5), window.UsedPercent.ObservedAt);
    }

    [Fact]
    public void OnlyFutureDatedEvent_IsUnavailableNoRecentEvent()
    {
        var future = CodexTestData.MakeLine(FetchedAt.AddMinutes(10), primaryUsed: 90m);
        var snapshot = CodexSessionParser.BuildSnapshot(new[] { future }, FetchedAt);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-recent-event", snapshot.StatusReasonCode);
    }

    [Fact]
    public void FutureEventWithinClockSkewAllowance_IsStillAccepted()
    {
        // Within the 2-minute skew allowance — real clocks disagree slightly, so it is a valid reading.
        var skewed = CodexTestData.MakeLine(FetchedAt.AddMinutes(1), primaryUsed: 42m);
        var snapshot = CodexSessionParser.BuildSnapshot(new[] { skewed }, FetchedAt);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        Assert.Equal(42m, Assert.Single(snapshot.Windows).UsedPercent.Value);
    }

    // ---- Zero usable events → Unavailable("no-recent-event") ------------------------------------

    public static IEnumerable<object[]> ZeroEventCorpora()
    {
        yield return new object[] { Array.Empty<string>() };
        yield return new object[] { new[] { "garbage not json\n{still not json}" } };
        // A token_count event whose primary is null (degenerate) — no usable reading.
        yield return new object[] { new[] { CodexTestData.MakeLine(new DateTimeOffset(2026, 07, 19, 10, 0, 0, TimeSpan.Zero), primaryNull: true) } };
        // A well-formed event_msg that is NOT a token_count event.
        yield return new object[] { new[] { CodexTestData.MakeLine(new DateTimeOffset(2026, 07, 19, 10, 0, 0, TimeSpan.Zero), payloadType: "agent_message") } };
    }

    [Theory]
    [MemberData(nameof(ZeroEventCorpora))]
    public void NoUsableEvents_ReturnsUnavailableNoRecentEvent(string[] tails)
    {
        var snapshot = CodexSessionParser.BuildSnapshot(tails, FetchedAt);

        Assert.Equal(CodexProvider.ProviderId, snapshot.ProviderId);
        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-recent-event", snapshot.StatusReasonCode);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(MetricState.Unavailable, snapshot.CreditsBalance.State);
        Assert.Equal("no-recent-event", snapshot.CreditsBalance.ReasonCode);
        Assert.Equal(MetricState.Unavailable, snapshot.PlanType.State);
    }

    // ---- Field-level edges ----------------------------------------------------------------------

    [Fact]
    public void MissingReset_LeavesUsedPercentAvailable_ButResetsAtNotApplicable()
    {
        var line = CodexTestData.MakeLine(
            new DateTimeOffset(2026, 07, 19, 10, 0, 0, TimeSpan.Zero),
            primaryUsed: 55m,
            primaryResetsAt: null,
            primaryResetsInSeconds: null);

        var snapshot = CodexSessionParser.BuildSnapshot(new[] { line }, FetchedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(MetricState.Available, window.UsedPercent.State);
        Assert.Equal(55m, window.UsedPercent.Value);
        Assert.Equal(MetricState.NotApplicable, window.ResetsAt.State); // one field degrades independently
    }

    [Fact]
    public void UsedPercent_PreservesUnroundedDecimal()
    {
        var line = CodexTestData.MakeLine(
            new DateTimeOffset(2026, 07, 19, 10, 0, 0, TimeSpan.Zero), primaryUsed: 73.4m);

        var snapshot = CodexSessionParser.BuildSnapshot(new[] { line }, FetchedAt);

        Assert.Equal(73.4m, Assert.Single(snapshot.Windows).UsedPercent.Value);
    }

    [Fact]
    public void CreditsBalance_UnparseableString_IsUnavailableSourceChanged()
    {
        var line = CodexTestData.MakeLine(
            new DateTimeOffset(2026, 07, 19, 10, 0, 0, TimeSpan.Zero), creditsBalance: "not-a-number");

        var snapshot = CodexSessionParser.BuildSnapshot(new[] { line }, FetchedAt);

        Assert.Equal(MetricState.Unavailable, snapshot.CreditsBalance.State);
        Assert.Equal("source-changed", snapshot.CreditsBalance.ReasonCode);
    }

    [Fact]
    public void CreditsBalance_NumericString_ParsesToDecimal()
    {
        var line = CodexTestData.MakeLine(
            new DateTimeOffset(2026, 07, 19, 10, 0, 0, TimeSpan.Zero), creditsBalance: "12.50");

        var snapshot = CodexSessionParser.BuildSnapshot(new[] { line }, FetchedAt);

        Assert.Equal(MetricState.Available, snapshot.CreditsBalance.State);
        Assert.Equal(12.50m, snapshot.CreditsBalance.Value);
    }

    // ---- Line-level tolerance -------------------------------------------------------------------

    [Fact]
    public void TryParseTokenCountLine_RejectsFragment_AcceptsValidLine()
    {
        Assert.False(CodexSessionParser.TryParseTokenCountLine("put_tokens\":45},\"rate_limits\":{", out var bad));
        Assert.Null(bad);

        var good = CodexTestData.MakeLine(new DateTimeOffset(2026, 07, 19, 10, 09, 25, 376, TimeSpan.Zero), primaryUsed: 4m);
        Assert.True(CodexSessionParser.TryParseTokenCountLine(good, out var evt));
        Assert.NotNull(evt);
        Assert.Equal(10080, evt.Primary.WindowMinutes);
        Assert.Equal(4m, evt.Primary.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 07, 19, 10, 09, 25, 376, TimeSpan.Zero), evt.Timestamp);
    }
}
