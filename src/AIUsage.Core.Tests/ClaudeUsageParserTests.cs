using System.Globalization;
using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Acceptance tests for the pure Claude parser, driven by the DEFINITIVE E1 capture
/// (<c>raw-capture-1.json</c>) plus synthetic bodies for the malformed / degradation axes
/// (spike <c>e1-findings.md</c>, DESIGN.md §4.2/§5). This is a high-risk unit — it parses a real,
/// undocumented, codename-laden payload — so the pinned semantics and every drift path are pinned here.
/// </summary>
public sealed class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 07, 20, 12, 00, 00, TimeSpan.Zero);

    private static DateTimeOffset Reset(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static ClaudeUsageWindow Session(ClaudeUsageParseResult r) =>
        Assert.Single(r.Windows, w => w.Window.WindowMinutes == 300);

    private static ClaudeUsageWindow WeeklyAll(ClaudeUsageParseResult r) =>
        Assert.Single(r.Windows, w => w.Window.WindowMinutes == 10080 && w.ScopeModel is null);

    private static ClaudeUsageWindow Scoped(ClaudeUsageParseResult r) =>
        Assert.Single(r.Windows, w => w.ScopeModel == "Fable");

    // ---- The DEFINITIVE E1 fixture ---------------------------------------------------------------

    [Fact]
    public void Fixture_ProducesFiveHourWeeklyAndPerModelScopedWindows_NotDrift()
    {
        var result = ClaudeUsageParser.Parse(ClaudeTestData.ReadFixture(), ObservedAt);

        Assert.False(result.IsDrift);
        Assert.Null(result.DriftReason);
        Assert.Equal(3, result.Windows.Count); // session + weekly_all + weekly_scoped; null buckets ignored

        // 5h window — from limits[session], percent preferred from the float five_hour.utilization (28.0).
        var session = Session(result);
        Assert.Equal("5h", session.Window.Label);
        Assert.Equal(MetricState.Available, session.Window.UsedPercent.State);
        Assert.Equal(28.0m, session.Window.UsedPercent.Value);
        Assert.Equal(ObservedAt, session.Window.UsedPercent.ObservedAt); // body has no observation time → fetch time
        Assert.Equal(MetricState.Available, session.Window.ResetsAt.State);
        Assert.Equal(Reset("2026-07-19T19:59:59.592703+00:00"), session.Window.ResetsAt.Value);
        Assert.False(session.IsActive);

        // Weekly (all) — float seven_day.utilization (37.0) preferred.
        var weekly = WeeklyAll(result);
        Assert.Equal("Weekly", weekly.Window.Label);
        Assert.Equal(37.0m, weekly.Window.UsedPercent.Value);
        Assert.Equal(Reset("2026-07-21T02:59:59.592724+00:00"), weekly.Window.ResetsAt.Value);

        // Per-model weekly — from weekly_scoped + scope.model.display_name "Fable", is_active true.
        var scoped = Scoped(result);
        Assert.Equal(10080, scoped.Window.WindowMinutes);
        Assert.Equal("Fable wk", scoped.Window.Label);
        Assert.Equal(43m, scoped.Window.UsedPercent.Value); // int percent (no float anchor for scoped)
        Assert.Equal(Reset("2026-07-21T02:59:59.593014+00:00"), scoped.Window.ResetsAt.Value);
        Assert.True(scoped.IsActive);

        // Server severity is carried as a tolerated cross-check.
        Assert.Equal("normal", scoped.Severity);

        // Credits: spend is present but disabled in the fixture → NotApplicable, never a fabricated zero.
        Assert.Equal(MetricState.NotApplicable, result.Credits.State);

        // The oauth/usage body carries no plan field → not-reported.
        Assert.Equal(MetricState.NotApplicable, result.PlanType.State);
        Assert.Equal("not-reported", result.PlanType.ReasonCode);
    }

    [Fact]
    public void Fixture_AllPercentsOnZeroToHundredScale_NotFractions()
    {
        var result = ClaudeUsageParser.Parse(ClaudeTestData.ReadFixture(), ObservedAt);

        foreach (var window in result.Windows)
        {
            Assert.Equal(MetricState.Available, window.Window.UsedPercent.State);
            Assert.InRange(window.Window.UsedPercent.Value, 1m, 100m); // 28 / 37 / 43 — never 0.28 etc.
        }
    }

    [Fact]
    public void Fixture_ToleratesNullCodenameBuckets_WithoutDrift()
    {
        // The real fixture carries seven_day_opus/seven_day_sonnet/tangelo/iguana_necktie/... all null.
        var result = ClaudeUsageParser.Parse(ClaudeTestData.ReadFixture(), ObservedAt);

        Assert.False(result.IsDrift);
        // The per-model window is sourced from weekly_scoped, NOT from a top-level per-model bucket.
        Assert.Equal("Fable", Scoped(result).ScopeModel);
        // A non-sensitive signature is recorded; it lists the null buckets by NAME (never a value).
        Assert.NotNull(result.SchemaSignature);
        Assert.Contains("tangelo:Null", result.SchemaSignature);
    }

    // ---- Float precision preferred over the int rounding -----------------------------------------

    [Fact]
    public void FloatUtilization_PreferredOverIntPercent_ForSessionAndWeeklyAll()
    {
        // limits int percents are the ROUNDED 12/34; the float utilizations carry the true precision.
        var body = ClaudeTestData.Body(
            fiveHour: ClaudeTestData.UtilObject(12.4),
            sevenDay: ClaudeTestData.UtilObject(34.6),
            limits: new[]
            {
                ClaudeTestData.Limit("session", percent: 12),
                ClaudeTestData.Limit("weekly_all", percent: 35),
            });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.Equal(12.4m, Session(result).Window.UsedPercent.Value);   // float, not the int 12
        Assert.Equal(34.6m, WeeklyAll(result).Window.UsedPercent.Value); // float, not the int 35
    }

    // ---- Drift: untrustable envelope -> all-NA source-changed ------------------------------------

    [Theory]
    [InlineData("not json at all {")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]              // JSON, but not an object
    [InlineData("\"a string\"")]    // JSON, but not an object
    public void UntrustableEnvelope_IsDriftSourceChanged_AllNa(string body)
    {
        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.True(result.IsDrift);
        Assert.Equal("source-changed", result.DriftReason);
        Assert.Empty(result.Windows);
        Assert.Equal(MetricState.Unavailable, result.Credits.State);
        Assert.Equal("source-changed", result.Credits.ReasonCode);
        Assert.Equal(MetricState.Unavailable, result.PlanType.State);
    }

    [Fact]
    public void EmptyLimitsAndNoFiveHour_IsDrift()
    {
        // Empty limits[] and NEITHER a five_hour nor a seven_day to fall back to.
        var body = ClaudeTestData.Body(limits: Array.Empty<Dictionary<string, object?>>(), nullBuckets: true);

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.True(result.IsDrift);
        Assert.Equal("source-changed", result.DriftReason);
        Assert.Empty(result.Windows);
        // The signature is still recorded (it WAS a JSON object) — the provider notes the shape change.
        Assert.NotNull(result.SchemaSignature);
    }

    // ---- Per-field degradation on an OTHERWISE usable body --------------------------------------

    [Fact]
    public void BadPercentOnOneLimit_MakesThatWindowNa_OthersFine()
    {
        // A typed-wrong percent on the scoped window; the session window is fully valid → not drift.
        var body = ClaudeTestData.Body(limits: new[]
        {
            ClaudeTestData.Limit("session", percent: 28),
            ClaudeTestData.Limit("weekly_scoped", percent: "oops", scopeModel: "Fable", isActive: true),
        });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.False(result.IsDrift);
        Assert.Equal(MetricState.Available, Session(result).Window.UsedPercent.State); // sibling untouched
        var scoped = Scoped(result);
        Assert.NotEqual(MetricState.Available, scoped.Window.UsedPercent.State);
        Assert.Equal("source-changed", scoped.Window.UsedPercent.ReasonCode); // present but typed-wrong = drift of that field
        Assert.Equal(MetricState.Available, scoped.Window.ResetsAt.State);     // the reset degrades independently
    }

    [Fact]
    public void OutOfRangePercent_IsNa_NotClamped()
    {
        var body = ClaudeTestData.Body(limits: new[]
        {
            ClaudeTestData.Limit("session", percent: 28),
            ClaudeTestData.Limit("weekly_scoped", percent: 150, scopeModel: "Fable"), // > 100
        });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.False(result.IsDrift);
        var scoped = Scoped(result);
        Assert.Equal(MetricState.Unavailable, scoped.Window.UsedPercent.State);
        Assert.Equal("out-of-range", scoped.Window.UsedPercent.ReasonCode);
        // NOT clamped: a non-Available metric has no readable value (default), never a silent 100.
        Assert.NotEqual(100m, scoped.Window.UsedPercent.Value);
    }

    [Fact]
    public void MissingReset_LeavesUsedPercentAvailable_ButResetNotApplicable()
    {
        var body = ClaudeTestData.Body(limits: new[]
        {
            ClaudeTestData.Limit("session", percent: 28),
            ClaudeTestData.Limit("weekly_scoped", percent: 43, scopeModel: "Fable", resetsAt: null),
        });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        var scoped = Scoped(result);
        Assert.Equal(MetricState.Available, scoped.Window.UsedPercent.State);
        Assert.Equal(43m, scoped.Window.UsedPercent.Value);
        Assert.Equal(MetricState.NotApplicable, scoped.Window.ResetsAt.State); // one field degrades alone
        Assert.Equal("not-reported", scoped.Window.ResetsAt.ReasonCode);
    }

    // ---- Fallback path: five_hour/seven_day when limits[] is absent -----------------------------

    [Fact]
    public void NoLimits_FallsBackToTopLevelFiveHourAndSevenDay()
    {
        var body = ClaudeTestData.Body(
            fiveHour: ClaudeTestData.UtilObject(28.0),
            sevenDay: ClaudeTestData.UtilObject(37.0),
            nullBuckets: true);

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.False(result.IsDrift);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(28.0m, Session(result).Window.UsedPercent.Value);
        Assert.Equal(37.0m, WeeklyAll(result).Window.UsedPercent.Value);
    }

    // ---- Credits -------------------------------------------------------------------------------

    [Fact]
    public void EnabledSpend_IsNotABalance_IsNotApplicable_NeverSurfacedAsCredits()
    {
        // `spend.used` is amount SPENT, not a balance, and carries no balance unit — it must NEVER be
        // presented as a credit balance (review P0-5 / Sol). Enabled spend → n/a "no-balance", not $12.34.
        var body = ClaudeTestData.Body(
            limits: new[] { ClaudeTestData.Limit("session", percent: 28) },
            spend: ClaudeTestData.EnabledSpend(amountMinor: 1234, exponent: 2));

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.Equal(MetricState.NotApplicable, result.Credits.State);
        Assert.Equal("no-balance", result.Credits.ReasonCode);
        // A non-Available metric has no readable value — the "$12.34" spend figure never leaks through.
        Assert.NotEqual(12.34m, result.Credits.Value);
    }

    [Fact]
    public void DisabledSpend_CreditsNotApplicable()
    {
        var body = ClaudeTestData.Body(
            limits: new[] { ClaudeTestData.Limit("session", percent: 28) },
            spend: ClaudeTestData.DisabledSpend());

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.Equal(MetricState.NotApplicable, result.Credits.State);
        Assert.Equal("disabled", result.Credits.ReasonCode);
    }

    [Fact]
    public void EnabledExtraUsage_WithoutPinnedSemantics_IsScaleUnpinned_NeverFabricated()
    {
        // extra_usage.is_enabled but E1 never pinned its value units → refuse to guess (HARD RULE).
        var body = ClaudeTestData.Body(
            limits: new[] { ClaudeTestData.Limit("session", percent: 28) },
            extraUsage: new Dictionary<string, object?> { ["is_enabled"] = true, ["used_credits"] = 500 });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.Equal(MetricState.NotApplicable, result.Credits.State);
        Assert.Equal("scale-unpinned", result.Credits.ReasonCode);
    }

    // ---- Composite window identity: weekly_all vs weekly_scoped never collide (review P0-1) ------

    [Fact]
    public void Fixture_TwoWeeklyWindows_ShareMinutes_ButHaveDistinctCompositeKeys()
    {
        var result = ClaudeUsageParser.Parse(ClaudeTestData.ReadFixture(), ObservedAt);

        Assert.Equal("300", Session(result).Window.Key);
        Assert.Equal("10080", WeeklyAll(result).Window.Key);
        Assert.Equal("10080:Fable", Scoped(result).Window.Key);

        // The two weekly windows share the authoritative minute-count but NOT the composite key — that is
        // what stops their history / severity / arming cross-contaminating downstream.
        Assert.Equal(WeeklyAll(result).Window.WindowMinutes, Scoped(result).Window.WindowMinutes);
        Assert.NotEqual(WeeklyAll(result).Window.Key, Scoped(result).Window.Key);
    }

    [Fact]
    public void WeeklyScoped_WithoutModelName_IsLabelledScopedWk_NotWeekly_AndKeyedDistinctly()
    {
        // A scoped window with no scope.model.display_name must NOT masquerade as the all-models "Weekly"
        // (review P0-1 / Sol): distinct label AND distinct key so it can never merge with weekly_all.
        var body = ClaudeTestData.Body(limits: new[]
        {
            ClaudeTestData.Limit("weekly_all", percent: 61),
            ClaudeTestData.Limit("weekly_scoped", percent: 84), // no scopeModel
        });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        var all = Assert.Single(result.Windows, w => w.Window.Label == "Weekly");
        var scoped = Assert.Single(result.Windows, w => w.Window.Label == "Scoped wk");
        Assert.NotEqual("Weekly", scoped.Window.Label);
        Assert.Equal("10080", all.Window.Key);
        Assert.Equal("10080:scoped", scoped.Window.Key);
        Assert.NotEqual(all.Window.Key, scoped.Window.Key);
    }

    [Fact]
    public void TwoNamelessWeeklyScoped_HaveDistinctKeys_NeverCollide()
    {
        // Hypothetical future shape: TWO weekly_scoped entries, NEITHER carrying a scope.model.display_name.
        // Both share 10080 minutes, so a single shared key would cross-contaminate their history / severity /
        // arming P0-1-style (review NEW-4). The first keeps the bare "scoped" key (backward-compatible); the
        // second is disambiguated by its ordinal. Assert.Single doubles as the collision check: were both keyed
        // "10080:scoped", each single-match lookup would fail on two matches.
        var body = ClaudeTestData.Body(limits: new[]
        {
            ClaudeTestData.Limit("weekly_scoped", percent: 40, resetsAt: "2026-07-21T02:00:00+00:00"),
            ClaudeTestData.Limit("weekly_scoped", percent: 84, resetsAt: "2026-07-22T02:00:00+00:00"),
        });

        var result = ClaudeUsageParser.Parse(body, ObservedAt);

        Assert.False(result.IsDrift);
        Assert.Equal(2, result.Windows.Count);

        var first = Assert.Single(result.Windows, w => w.Window.Key == "10080:scoped");
        var second = Assert.Single(result.Windows, w => w.Window.Key == "10080:scoped#1");
        Assert.NotEqual(first.Window.Key, second.Window.Key);

        // Each retains its OWN reading — proof the two windows never merged into one bucket.
        Assert.Equal(40m, first.Window.UsedPercent.Value);
        Assert.Equal(84m, second.Window.UsedPercent.Value);
        Assert.NotEqual(first.Window.ResetsAt.Value, second.Window.ResetsAt.Value);
    }

    // ---- Schema signature is non-sensitive (names/kinds only, never values) ---------------------

    [Fact]
    public void SchemaSignature_ListsNamesAndKinds_NeverValues()
    {
        var result = ClaudeUsageParser.Parse(ClaudeTestData.ReadFixture(), ObservedAt);

        Assert.NotNull(result.SchemaSignature);
        Assert.Contains("limits:Array", result.SchemaSignature);
        Assert.Contains("five_hour:Object", result.SchemaSignature);
        // No leaked values: the "Fable" model name and the "28" percent never appear in the signature.
        Assert.DoesNotContain("Fable", result.SchemaSignature);
        Assert.DoesNotContain("28", result.SchemaSignature);
    }
}
