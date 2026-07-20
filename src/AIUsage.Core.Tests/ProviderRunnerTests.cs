using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Proves a single <see cref="ProviderRunner"/> contains every fault (DESIGN.md §4): a throwing or
/// faulting fetch becomes <c>Unavailable("fetch-error")</c>, a hanging fetch is bounded by the
/// timeout into <c>Unavailable("timeout")</c>, and the loop always shuts down gracefully. Tests are
/// deterministic: each awaits the actual published outcome (never a sleep-then-assert), guarded by a
/// generous safety timeout so a broken implementation fails fast instead of hanging.
/// </summary>
public sealed class ProviderRunnerTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task ThrowingProvider_PublishesUnavailable_WithFetchErrorReason()
    {
        using var provider = new FakeUsageProvider("bad", FastInterval, FakeUsageProvider.Mode.Throws);
        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var runner = new ProviderRunner(provider, store, TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        await watcher
            .WaitAsync(events => events.Any(s => s.ProviderId == "bad" && s.Status == SourceStatus.Unavailable))
            .WaitAsync(SafetyTimeout);

        cts.Cancel();
        await run.WaitAsync(SafetyTimeout);

        var first = watcher.Events.First(s => s.ProviderId == "bad");
        Assert.Equal(SourceStatus.Unavailable, first.Status);
        Assert.Equal("fetch-error", first.StatusReasonCode);
        Assert.Equal(MetricState.Unavailable, first.CreditsBalance.State);
    }

    [Fact]
    public async Task FaultingProvider_PublishesUnavailable_WithFetchErrorReason()
    {
        // The awaited-fault path (a returned faulted Task, not a synchronous throw).
        using var provider = new FakeUsageProvider("bad", FastInterval, FakeUsageProvider.Mode.FaultsAsync);
        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var runner = new ProviderRunner(provider, store, TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        await watcher
            .WaitAsync(events => events.Any(s => s.Status == SourceStatus.Unavailable && s.StatusReasonCode == "fetch-error"))
            .WaitAsync(SafetyTimeout);

        cts.Cancel();
        await run.WaitAsync(SafetyTimeout);
    }

    [Fact]
    public async Task HangingProvider_IsBoundedByTimeout_PublishesUnavailableTimeout()
    {
        using var provider = new FakeUsageProvider("slow", FastInterval, FakeUsageProvider.Mode.Hangs);
        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);

        // A short per-fetch timeout: the fetch never completes on its own, so the timeout is the
        // ONLY thing that ends the wait — the outcome is deterministic regardless of exact timing.
        var runner = new ProviderRunner(provider, store, TimeProvider.System, TimeSpan.FromMilliseconds(150));

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        await watcher
            .WaitAsync(events => events.Any(s => s.ProviderId == "slow" && s.StatusReasonCode == "timeout"))
            .WaitAsync(SafetyTimeout);

        cts.Cancel();
        await run.WaitAsync(SafetyTimeout);

        var timeout = watcher.Events.First(s => s.ProviderId == "slow");
        Assert.Equal(SourceStatus.Unavailable, timeout.Status);
        Assert.Equal("timeout", timeout.StatusReasonCode);
    }

    [Fact]
    public async Task OkProvider_PublishesOkSnapshots_Repeatedly()
    {
        using var provider = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok, usedPercent: 55m);
        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var runner = new ProviderRunner(provider, store, TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        await watcher
            .WaitAsync(events => events.Count(s => s.ProviderId == "good" && s.Status == SourceStatus.Ok) >= 3)
            .WaitAsync(SafetyTimeout);

        cts.Cancel();
        await run.WaitAsync(SafetyTimeout);

        var ok = watcher.Events.First(s => s.ProviderId == "good");
        Assert.Equal(SourceStatus.Ok, ok.Status);
        Assert.Equal(55m, ok.Windows[0].UsedPercent.Value);
    }

    [Fact]
    public async Task Cancellation_StopsTheLoop_Gracefully_WithoutThrowing()
    {
        using var provider = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok);
        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var runner = new ProviderRunner(provider, store, TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        await watcher
            .WaitAsync(events => events.Count > 0)
            .WaitAsync(SafetyTimeout);

        cts.Cancel();

        // RunAsync completes normally — cancellation is not an exception surfaced to the caller.
        await run.WaitAsync(SafetyTimeout);
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveFetchTimeout()
    {
        using var provider = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok);
        var store = new SnapshotStore();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProviderRunner(provider, store, TimeProvider.System, TimeSpan.Zero));
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotKillTheLoop_KeepsPublishing()
    {
        // A subscriber that throws on EVERY publish must not terminate the loop: the runner's isolation
        // contract now wraps Publish (and the inter-fetch delay), so the loop keeps producing snapshots.
        using var provider = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok, usedPercent: 42m);
        var store = new SnapshotStore();
        var publishes = new System.Runtime.CompilerServices.StrongBox<int>(0);
        store.SnapshotChanged += (_, _) =>
        {
            System.Threading.Interlocked.Increment(ref publishes.Value);
            throw new InvalidOperationException("boom from a subscriber");
        };

        var runner = new ProviderRunner(provider, store, TimeProvider.System);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);

        // If the throwing subscriber killed the loop, this count would stall at 1 and the wait would expire.
        var deadline = DateTime.UtcNow + SafetyTimeout;
        while (System.Threading.Volatile.Read(ref publishes.Value) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await run.WaitAsync(SafetyTimeout);

        Assert.True(System.Threading.Volatile.Read(ref publishes.Value) >= 3, "the loop stopped publishing after a subscriber threw");
        Assert.True(run.IsCompletedSuccessfully); // and it still shuts down cleanly
    }
}
