using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Tests the <see cref="CodexProvider"/> I/O shell: sessions-dir resolution (incl. <c>CODEX_HOME</c>),
/// the absent-dir and empty-corpus Unavailable states, real file reads through to a snapshot, candidate
/// fall-through, and the 14-day age cap. Parsing/mapping correctness lives in
/// <see cref="CodexSessionParserTests"/>; these assert the file layer feeds it correctly.
/// </summary>
public sealed class CodexProviderTests
{
    private static CodexProvider Provider(string sessionsDirectory)
        => new(new FakeTimeProvider(DateTimeOffset.UtcNow), sessionsDirectory);

    [Fact]
    public async Task AbsentSessionsDir_ReturnsUnavailableNoSessionsDir()
    {
        var missing = Path.Combine(Path.GetTempPath(), "codex-missing-" + Guid.NewGuid().ToString("N"));
        var snapshot = await Provider(missing).FetchAsync(CancellationToken.None);

        Assert.Equal(CodexProvider.ProviderId, snapshot.ProviderId);
        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-sessions-dir", snapshot.StatusReasonCode);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(MetricState.Unavailable, snapshot.CreditsBalance.State);
        Assert.Equal(MetricState.Unavailable, snapshot.PlanType.State);
    }

    [Fact]
    public async Task EmptySessionsDir_ReturnsUnavailableNoRecentEvent()
    {
        using var dir = new TempDir();
        var snapshot = await Provider(dir.Root).FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-recent-event", snapshot.StatusReasonCode);
    }

    [Fact]
    public async Task ReadsWeeklyFixtureFile_ProducesLiveWeeklyReading()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Root, "session.jsonl"), CodexTestData.ReadFixture("fixture-weekly-only.jsonl"));

        var snapshot = await Provider(dir.Root).FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(WindowKind.Weekly, WindowClassifier.Classify(window.WindowMinutes));
        Assert.Equal(4.0m, window.UsedPercent.Value);
        Assert.Equal("plus", snapshot.PlanType.Value);
    }

    [Fact]
    public async Task FallsThroughNoEventFile_AndPicksNewestEmbeddedEventAcrossFiles()
    {
        using var dir = new TempDir();
        // A file with no token_count events at all — must be skipped, not returned as empty.
        File.WriteAllText(Path.Combine(dir.Root, "no-events.jsonl"), "{\"type\":\"session_meta\"}\n");

        var baseTime = new DateTimeOffset(2026, 07, 19, 09, 00, 00, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(dir.Root, "older.jsonl"), CodexTestData.MakeLine(baseTime, primaryUsed: 20m));
        File.WriteAllText(Path.Combine(dir.Root, "newer.jsonl"), CodexTestData.MakeLine(baseTime.AddHours(1), primaryUsed: 77m));

        var snapshot = await Provider(dir.Root).FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, snapshot.Status);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(77m, window.UsedPercent.Value); // newest embedded timestamp across the candidate set
    }

    [Fact]
    public async Task FileOlderThanAgeCap_IsExcluded_YieldingNoRecentEvent()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);

        var path = Path.Combine(dir.Root, "stale.jsonl");
        File.WriteAllText(path, CodexTestData.ReadFixture("fixture-weekly-only.jsonl"));
        File.SetLastWriteTimeUtc(path, (now - TimeSpan.FromDays(30)).UtcDateTime);

        var snapshot = await new CodexProvider(clock, dir.Root).FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Unavailable, snapshot.Status);
        Assert.Equal("no-recent-event", snapshot.StatusReasonCode); // the sole file is beyond the 14-day cap
    }

    [Fact]
    public async Task HonoursCodexHomeEnvVar_ForDefaultResolution()
    {
        using var dir = new TempDir();
        var sessions = Path.Combine(dir.Root, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "session.jsonl"), CodexTestData.ReadFixture("fixture-weekly-only.jsonl"));

        var prior = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", dir.Root);

            var provider = new CodexProvider(new FakeTimeProvider(DateTimeOffset.UtcNow)); // null override → resolve from env
            Assert.Equal(sessions, provider.SessionsDirectory);

            var snapshot = await provider.FetchAsync(CancellationToken.None);
            Assert.Equal(SourceStatus.Ok, snapshot.Status);
            Assert.Equal("plus", snapshot.PlanType.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", prior);
        }
    }

    [Fact]
    public void MinInterval_Is60Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(60), Provider(Path.GetTempPath()).MinInterval);
}
