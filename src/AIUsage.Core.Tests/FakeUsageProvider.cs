namespace AIUsage.Core.Tests;

/// <summary>
/// A controllable <see cref="IUsageProvider"/> for the isolation tests. Each mode exercises a
/// distinct fault shape the runner must contain:
/// <list type="bullet">
///   <item><see cref="Mode.Ok"/> — returns a valid <see cref="SourceStatus.Ok"/> snapshot.</item>
///   <item><see cref="Mode.Throws"/> — throws SYNCHRONOUSLY, before yielding a Task.</item>
///   <item><see cref="Mode.FaultsAsync"/> — returns an already-faulted Task (the awaited-fault path).</item>
///   <item><see cref="Mode.Hangs"/> — returns a Task that never completes and IGNORES its
///   cancellation token, proving the runner's timeout bounds even an uncooperative fetch.</item>
/// </list>
/// Hand-rolled so the test project keeps zero third-party packages beyond xUnit + the test SDK.
/// </summary>
internal sealed class FakeUsageProvider : IUsageProvider, IDisposable
{
    internal enum Mode
    {
        Ok,
        Throws,
        FaultsAsync,
        Hangs,
    }

    private readonly Mode _mode;
    private readonly decimal _usedPercent;
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<ProviderSnapshot>> _hangs = new();
    private int _callCount;

    public FakeUsageProvider(string id, TimeSpan minInterval, Mode mode, decimal usedPercent = 50m)
    {
        Id = id;
        MinInterval = minInterval;
        _mode = mode;
        _usedPercent = usedPercent;
    }

    public string Id { get; }

    public TimeSpan MinInterval { get; }

    /// <summary>How many times <see cref="FetchAsync"/> has been entered.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);

        switch (_mode)
        {
            case Mode.Throws:
                throw new InvalidOperationException("simulated synchronous provider failure");

            case Mode.FaultsAsync:
                return Task.FromException<ProviderSnapshot>(
                    new InvalidOperationException("simulated asynchronous provider failure"));

            case Mode.Hangs:
                // Deliberately does NOT observe the token: only the runner's timeout can bound it.
                var tcs = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _hangs.Add(tcs);
                }

                return tcs.Task;

            default:
                return Task.FromResult(BuildOk(Id, _usedPercent));
        }
    }

    /// <summary>Release any outstanding hang tasks so the test process leaves nothing dangling.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var tcs in _hangs)
            {
                tcs.TrySetCanceled();
            }

            _hangs.Clear();
        }
    }

    internal static ProviderSnapshot BuildOk(string id, decimal usedPercent)
    {
        var now = new DateTimeOffset(2026, 07, 19, 14, 00, 00, TimeSpan.Zero);
        return new ProviderSnapshot(
            ProviderId: id,
            FetchedAt: now,
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: new[]
            {
                new UsageWindow(
                    WindowMinutes: 10080,
                    Label: WindowClassifier.Label(10080),
                    UsedPercent: Metric.Available(usedPercent, now),
                    ResetsAt: Metric.Available(now.AddDays(3), now)),
            },
            CreditsBalance: Metric.Available(12.4m, now),
            PlanType: Metric.Available("pro", now));
    }
}
