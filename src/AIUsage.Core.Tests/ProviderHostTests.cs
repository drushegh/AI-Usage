using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// The ISOLATION acceptance tests (DESIGN.md §4): with providers running side by side under one
/// <see cref="ProviderHost"/>, one provider's throw or hang produces its own honest n/a snapshot and
/// NEVER touches, blocks, or delays a sibling — which keeps publishing Ok throughout. Also proves the
/// "third provider = one registration line" property and the startup n/a state.
/// </summary>
public sealed class ProviderHostTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task ThrowingProvider_DoesNotAffectSibling()
    {
        using var bad = new FakeUsageProvider("bad", FastInterval, FakeUsageProvider.Mode.Throws);
        using var good = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok, usedPercent: 42m);

        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var host = new ProviderHost(store, TimeProvider.System);
        host.Register(bad);
        host.Register(good);
        host.Start();

        try
        {
            // The failing provider emits its honest n/a AND the healthy sibling keeps publishing Ok.
            await watcher
                .WaitAsync(events =>
                    events.Any(s => s.ProviderId == "bad" && s.StatusReasonCode == "fetch-error")
                    && events.Count(s => s.ProviderId == "good" && s.Status == SourceStatus.Ok) >= 3)
                .WaitAsync(SafetyTimeout);
        }
        finally
        {
            await host.StopAsync();
        }

        // Every "good" snapshot is Ok — the sibling's failure never leaked into it.
        Assert.All(
            watcher.Events.Where(s => s.ProviderId == "good"),
            s => Assert.Equal(SourceStatus.Ok, s.Status));
        Assert.True(store.TryGet("good", out var goodSnap));
        Assert.Equal(SourceStatus.Ok, goodSnap.Status);
        Assert.True(store.TryGet("bad", out var badSnap));
        Assert.Equal("fetch-error", badSnap.StatusReasonCode);
    }

    [Fact]
    public async Task HangingProvider_DoesNotDelaySibling()
    {
        using var slow = new FakeUsageProvider("slow", FastInterval, FakeUsageProvider.Mode.Hangs);
        using var good = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok);

        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);

        // 'slow' hangs on every fetch; only the 500ms per-fetch timeout ends each wait. 'good' polls
        // every ~10ms, so it publishes many times before the first hang even times out — proving the
        // hang neither blocks nor delays the sibling.
        var host = new ProviderHost(store, TimeProvider.System, fetchTimeout: TimeSpan.FromMilliseconds(500));
        host.Register(slow);
        host.Register(good);
        host.Start();

        try
        {
            await watcher
                .WaitAsync(events => events.Any(s => s.ProviderId == "slow" && s.StatusReasonCode == "timeout"))
                .WaitAsync(SafetyTimeout);
        }
        finally
        {
            await host.StopAsync();
        }

        var events = watcher.Events;
        var firstTimeoutIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].ProviderId == "slow" && events[i].StatusReasonCode == "timeout")
            {
                firstTimeoutIndex = i;
                break;
            }
        }

        Assert.True(firstTimeoutIndex >= 0);

        // The sibling produced multiple Ok snapshots BEFORE the hang's first timeout landed — it was
        // never gated on the stuck provider. (Huge margin: ~50 good publishes per 500ms hang.)
        var goodBeforeTimeout = events.Take(firstTimeoutIndex)
            .Count(s => s.ProviderId == "good" && s.Status == SourceStatus.Ok);
        Assert.True(
            goodBeforeTimeout >= 2,
            $"expected the sibling to publish while the provider hung; saw {goodBeforeTimeout} good snapshots before the timeout");
    }

    [Fact]
    public async Task ThirdProvider_IsExactlyOneRegistrationLine()
    {
        using var codex = new FakeUsageProvider("codex", FastInterval, FakeUsageProvider.Mode.Ok);
        using var claude = new FakeUsageProvider("claude", FastInterval, FakeUsageProvider.Mode.Ok);
        using var third = new FakeUsageProvider("third", FastInterval, FakeUsageProvider.Mode.Ok);

        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var host = new ProviderHost(store, TimeProvider.System);

        host.Register(codex);
        host.Register(claude);
        host.Register(third); // adding a provider is exactly this one line — no plugin loading.

        Assert.Equal(3, host.ProviderCount);
        host.Start();

        try
        {
            await watcher
                .WaitAsync(events =>
                    events.Any(s => s.ProviderId == "codex")
                    && events.Any(s => s.ProviderId == "claude")
                    && events.Any(s => s.ProviderId == "third"))
                .WaitAsync(SafetyTimeout);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.True(store.TryGet("third", out var thirdSnap));
        Assert.Equal(SourceStatus.Ok, thirdSnap.Status);
    }

    [Fact]
    public void BeforeStart_StoreHasNoSnapshotForAnyProvider()
    {
        using var codex = new FakeUsageProvider("codex", FastInterval, FakeUsageProvider.Mode.Ok);
        var store = new SnapshotStore();
        var host = new ProviderHost(store, TimeProvider.System);
        host.Register(codex);

        // Registered but not started: startup = n/a. The store is empty until the first fetch runs.
        Assert.False(store.TryGet("codex", out _));
        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public async Task Register_AfterStart_Throws()
    {
        using var codex = new FakeUsageProvider("codex", FastInterval, FakeUsageProvider.Mode.Ok);
        using var late = new FakeUsageProvider("late", FastInterval, FakeUsageProvider.Mode.Ok);
        var store = new SnapshotStore();
        var host = new ProviderHost(store, TimeProvider.System);
        host.Register(codex);
        host.Start();

        try
        {
            Assert.Throws<InvalidOperationException>(() => host.Register(late));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_StopsAllRunners_AndIsSafeWhenNeverStarted()
    {
        using var good = new FakeUsageProvider("good", FastInterval, FakeUsageProvider.Mode.Ok);
        var store = new SnapshotStore();
        using var watcher = new SnapshotWatcher(store);
        var host = new ProviderHost(store, TimeProvider.System);
        host.Register(good);

        // Safe to stop a host that never started.
        await host.StopAsync();

        host.Start();
        await watcher.WaitAsync(events => events.Count > 0).WaitAsync(SafetyTimeout);
        await host.StopAsync();

        // After StopAsync the loops are wound down; the count stops climbing.
        var countAfterStop = watcher.CountMatching(_ => true);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(countAfterStop, watcher.CountMatching(_ => true));
    }
}
