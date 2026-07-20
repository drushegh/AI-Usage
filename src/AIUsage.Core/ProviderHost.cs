namespace AIUsage.Core;

/// <summary>
/// Registers N providers — one <see cref="ProviderRunner"/> each — and starts/stops them together
/// (DESIGN.md §3/§4). Adding a third provider is exactly one <see cref="Register"/> line plus its
/// compile-time adapter; there is deliberately no plugin loading into a process that displays trusted
/// usage data. The runners share nothing but the injected <see cref="SnapshotStore"/>.
/// </summary>
public sealed class ProviderHost
{
    private readonly SnapshotStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _fetchTimeout;
    private readonly List<ProviderRunner> _runners = new();
    private readonly List<Task> _runnerTasks = new();
    private CancellationTokenSource? _cts;
    private bool _started;

    /// <param name="store">The shared store every runner publishes into and the UI reads from.</param>
    /// <param name="timeProvider">Clock/timer seam handed to every runner.</param>
    /// <param name="fetchTimeout">Optional per-fetch timeout applied to every runner (default <see cref="ProviderRunner.DefaultFetchTimeout"/>).</param>
    public ProviderHost(SnapshotStore store, TimeProvider timeProvider, TimeSpan? fetchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _timeProvider = timeProvider;
        _fetchTimeout = fetchTimeout;
    }

    /// <summary>Number of providers registered so far.</summary>
    public int ProviderCount => _runners.Count;

    /// <summary>
    /// Register one provider (creating its runner). Call once per provider — this is the "one
    /// registration line" that adds a source. Must be called before <see cref="Start"/>.
    /// </summary>
    public void Register(IUsageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_started)
        {
            throw new InvalidOperationException("Cannot register a provider after the host has started.");
        }

        _runners.Add(new ProviderRunner(provider, _store, _timeProvider, _fetchTimeout));
    }

    /// <summary>
    /// Start every registered runner on its own worker. Each loop is independent — a synchronously
    /// misbehaving or hanging provider can neither block this call nor stall a sibling.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("The host is already started.");
        }

        _started = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        foreach (var runner in _runners)
        {
            _runnerTasks.Add(Task.Run(() => runner.RunAsync(token), token));
        }
    }

    /// <summary>
    /// Signal cancellation and wait for every runner to wind down gracefully. Idempotent-safe to call
    /// when never started. Because runners never throw out, this completes without surfacing an error.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await Task.WhenAll(_runnerTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A runner started from an already-cancelled token surfaces as a cancelled task here;
            // that is the requested shutdown, not a failure.
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _runnerTasks.Clear();
            _started = false;
        }
    }
}
