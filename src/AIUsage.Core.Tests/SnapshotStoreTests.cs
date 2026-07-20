using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Proves the store contract (DESIGN.md §4): startup = n/a (no fabricated placeholder), atomic
/// last-writer-wins per provider, a point-in-time <see cref="SnapshotStore.Snapshots"/> view, and
/// <see cref="SnapshotStore.SnapshotChanged"/> firing with the exact published snapshot — under
/// concurrent publishers and readers.
/// </summary>
public sealed class SnapshotStoreTests
{
    private static ProviderSnapshot Ok(string id, decimal usedPercent = 50m)
        => FakeUsageProvider.BuildOk(id, usedPercent);

    private static readonly DateTimeOffset T0 = new(2026, 07, 20, 12, 00, 00, TimeSpan.Zero);

    private static ProviderSnapshot AtTime(string id, DateTimeOffset fetchedAt, decimal usedPercent) => new(
        ProviderId: id,
        FetchedAt: fetchedAt,
        Status: SourceStatus.Ok,
        StatusReasonCode: null,
        Windows: new[]
        {
            new UsageWindow(
                WindowMinutes: 10080,
                Label: WindowClassifier.Label(10080),
                UsedPercent: Metric.Available(usedPercent, fetchedAt),
                ResetsAt: Metric.Available(fetchedAt.AddDays(3), fetchedAt)),
        },
        CreditsBalance: Metric.Available(1m, fetchedAt),
        PlanType: Metric.Available("pro", fetchedAt));

    [Fact]
    public void BeforeFirstPublish_TryGet_ReturnsFalse_AndNoSnapshot()
    {
        // Startup = n/a: the store never invents a stale or zero-valued reading. "No observation
        // yet" is an explicit absence the UI renders as n/a.
        var store = new SnapshotStore();

        Assert.False(store.TryGet("codex", out var snapshot));
        Assert.Null(snapshot);
    }

    [Fact]
    public void BeforeFirstPublish_Snapshots_IsEmpty()
    {
        var store = new SnapshotStore();

        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public void Publish_ThenTryGet_ReturnsThatSnapshot()
    {
        var store = new SnapshotStore();
        var published = Ok("codex", 73m);

        store.Publish(published);

        Assert.True(store.TryGet("codex", out var got));
        Assert.Same(published, got);
    }

    [Fact]
    public void Publish_IsLastWriterWins_PerProvider()
    {
        var store = new SnapshotStore();
        var first = Ok("codex", 10m);
        var second = Ok("codex", 20m);

        store.Publish(first);
        store.Publish(second);

        Assert.True(store.TryGet("codex", out var got));
        Assert.Same(second, got);
    }

    [Fact]
    public void Publish_DropsAnOlderSnapshot_KeepsTheNewer()
    {
        // A manual refresh racing the loop can arrive with an OLDER FetchedAt than what is already stored;
        // the store must keep the newer reading so the displayed number and footer age never step backwards.
        var store = new SnapshotStore();
        var newer = AtTime("codex", T0 + TimeSpan.FromSeconds(30), 20m);
        var older = AtTime("codex", T0, 10m);

        store.Publish(newer);

        var announced = 0;
        store.SnapshotChanged += (_, _) => announced++;
        store.Publish(older); // the stale racer

        Assert.True(store.TryGet("codex", out var got));
        Assert.Same(newer, got);   // the newer reading is retained
        Assert.Equal(0, announced); // the dropped publish is not announced (nothing changed)
    }

    [Fact]
    public void Publish_AcceptsEqualOrNewerSnapshot()
    {
        var store = new SnapshotStore();

        store.Publish(AtTime("codex", T0, 10m));
        store.Publish(AtTime("codex", T0, 15m));                           // equal FetchedAt → newest-wins
        store.Publish(AtTime("codex", T0 + TimeSpan.FromSeconds(1), 20m)); // strictly newer → wins

        Assert.True(store.TryGet("codex", out var got));
        Assert.Equal(20m, got!.Windows[0].UsedPercent.Value);
    }

    [Fact]
    public void Publish_KeepsProvidersIndependent()
    {
        var store = new SnapshotStore();
        var codex = Ok("codex", 30m);
        var claude = Ok("claude", 40m);

        store.Publish(codex);
        store.Publish(claude);

        Assert.True(store.TryGet("codex", out var gotCodex));
        Assert.True(store.TryGet("claude", out var gotClaude));
        Assert.Same(codex, gotCodex);
        Assert.Same(claude, gotClaude);
        Assert.Equal(2, store.Snapshots.Count);
    }

    [Fact]
    public void Snapshots_IsAPointInTimeCopy_NotALiveView()
    {
        var store = new SnapshotStore();
        store.Publish(Ok("codex"));

        var view = store.Snapshots;
        Assert.Single(view);

        // A publish AFTER the view was taken must not mutate the already-handed-out copy.
        store.Publish(Ok("claude"));
        Assert.Single(view);
        Assert.Equal(2, store.Snapshots.Count);
    }

    [Fact]
    public void Publish_RaisesSnapshotChanged_WithTheNewSnapshot()
    {
        var store = new SnapshotStore();
        var published = Ok("codex", 61m);
        ProviderSnapshot? received = null;

        store.SnapshotChanged += (_, e) => received = e.Snapshot;
        store.Publish(published);

        Assert.Same(published, received);
    }

    [Fact]
    public async Task Publish_ConcurrentWritersAndReaders_StayConsistent_LastWriterWins()
    {
        // Many publishers hammer the same and different providers while readers scan the store.
        // The store must never throw, never expose a torn map, and settle on a whole published
        // snapshot per provider (atomic swap).
        var store = new SnapshotStore();
        const int perProvider = 500;
        var ids = new[] { "codex", "claude", "third" };
        using var cts = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var kvp in store.Snapshots)
                {
                    // A read must always see a self-consistent snapshot: its key matches its value's
                    // ProviderId (proof the swap is atomic, never half-applied).
                    Assert.Equal(kvp.Key, kvp.Value.ProviderId);
                }

                foreach (var id in ids)
                {
                    if (store.TryGet(id, out var snap))
                    {
                        Assert.Equal(id, snap.ProviderId);
                    }
                }
            }
        })).ToArray();

        var writers = ids.Select(id => Task.Run(() =>
        {
            for (var i = 0; i < perProvider; i++)
            {
                store.Publish(Ok(id, i));
            }
        })).ToArray();

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));

        foreach (var id in ids)
        {
            Assert.True(store.TryGet(id, out var snap));
            Assert.Equal(id, snap.ProviderId);
            // The winning value is one that was actually published (0..perProvider-1), never torn.
            Assert.Equal(MetricState.Available, snap.Windows[0].UsedPercent.State);
            Assert.InRange(snap.Windows[0].UsedPercent.Value, 0m, perProvider - 1);
        }
    }
}
