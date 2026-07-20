namespace AIUsage.Core;

/// <summary>
/// Runs ONE <see cref="IUsageProvider"/> in its own async loop (DESIGN.md §4). Each iteration:
/// fetch (bounded by a per-fetch timeout), publish the result, then wait <see cref="IUsageProvider.MinInterval"/>.
/// The loop's guarantee is <b>total isolation</b>: any exception the provider throws — or a fetch that
/// hangs past the timeout — is converted into an <see cref="SourceStatus.Unavailable"/> snapshot and
/// published; the loop itself never throws out and never crashes. One provider's failure therefore
/// cannot touch, delay, or degrade any sibling loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timing seam.</b> All waits go through the injected <see cref="TimeProvider"/> (dotnet-development
/// standard; DESIGN.md §4) — the inter-fetch delay via <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// and the per-fetch timeout via a companion delay raced against the fetch with <see cref="Task.WhenAny(Task[])"/>.
/// The timeout is enforced with <see cref="Task.WhenAny(Task[])"/> rather than by cancelling the fetch,
/// so even a fetch that ignores its cancellation token is still bounded — the runner stops waiting and
/// reports "timeout" regardless of whether the provider cooperates.
/// </para>
/// <para>
/// This type is UI-free by design: the WPF-dispatcher marshalling lives in the Tray layer, which
/// subscribes to <see cref="SnapshotStore.SnapshotChanged"/>.
/// </para>
/// </remarks>
public sealed class ProviderRunner
{
    /// <summary>Sensible default per-fetch timeout when the caller does not specify one.</summary>
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fallback pause used only when the injected inter-fetch delay itself faults (a broken timer). Bounds
    /// the loop so it can never hot-spin, without terminating it (the isolation contract).
    /// </summary>
    private static readonly TimeSpan DelayFaultFallback = TimeSpan.FromSeconds(1);

    private readonly IUsageProvider _provider;
    private readonly SnapshotStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _fetchTimeout;

    /// <param name="provider">The single provider this loop drives.</param>
    /// <param name="store">The store each snapshot is published into.</param>
    /// <param name="timeProvider">Clock/timer seam for all delays and snapshot timestamps.</param>
    /// <param name="fetchTimeout">Upper bound on a single fetch; defaults to <see cref="DefaultFetchTimeout"/>. Must be positive.</param>
    public ProviderRunner(
        IUsageProvider provider,
        SnapshotStore store,
        TimeProvider timeProvider,
        TimeSpan? fetchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _fetchTimeout = fetchTimeout ?? DefaultFetchTimeout;
        if (_fetchTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fetchTimeout), _fetchTimeout, "The per-fetch timeout must be positive.");
        }

        _provider = provider;
        _store = store;
        _timeProvider = timeProvider;
    }

    /// <summary>The id of the provider this runner drives (its store key).</summary>
    public string ProviderId => _provider.Id;

    /// <summary>
    /// Drive the loop until <paramref name="cancellationToken"/> is signalled. Fetches first (so a
    /// fresh session gets an observation without waiting a full interval), then spaces subsequent
    /// fetches by <see cref="IUsageProvider.MinInterval"/>. Completes normally on cancellation — it
    /// never surfaces an exception to the caller.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ProviderSnapshot snapshot;
            try
            {
                snapshot = await FetchBoundedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception)
            {
                // Absolute backstop: even a defect in the bounding logic above degrades to n/a
                // rather than escaping the loop. A loop must never crash (DESIGN.md §4).
                snapshot = BuildUnavailable("fetch-error");
            }

            // Publish and the inter-fetch delay are INSIDE the loop's isolation guarantee too: a throwing
            // subscriber or a faulting timer must degrade to a safe continue, never terminate the loop
            // (DESIGN.md §4 "the loop never throws out"). The fetch above is already contained; these two
            // seams are the remaining ways an exception could escape RunAsync.
            try
            {
                _store.Publish(snapshot);
            }
            catch (Exception)
            {
                // A subscriber threw. The snapshot is already stored; the fault is the subscriber's, and it
                // must not kill this provider's loop or delay any sibling. Swallow and carry on.
            }

            try
            {
                await DelayAsync(_provider.MinInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A timer/interval fault must not terminate the loop. Fall back to a real, bounded delay so a
                // persistently broken injected timer degrades to a safe pace instead of a hot spin.
                try
                {
                    await Task.Delay(DelayFaultFallback, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Even the fallback delay faulted (should be impossible with the default timer) — continue;
                    // the next fetch is itself timeout-bounded, so the loop still cannot busy-spin unbounded.
                }
            }
        }
    }

    /// <summary>
    /// Run one fetch bounded by <see cref="_fetchTimeout"/>. Returns a snapshot for every outcome
    /// except a genuine shutdown (which re-throws <see cref="OperationCanceledException"/> so
    /// <see cref="RunAsync"/> can break cleanly): a provider fault → <c>Unavailable("fetch-error")</c>,
    /// a fetch that outlasts the timeout → <c>Unavailable("timeout")</c>.
    /// </summary>
    private async Task<ProviderSnapshot> FetchBoundedAsync(CancellationToken loopToken)
    {
        loopToken.ThrowIfCancellationRequested();

        // fetchCts cancels a (cooperative) fetch on timeout/shutdown; timeoutCts cancels the
        // companion timeout delay once the fetch has won. Both are linked to the loop token so a
        // shutdown tears everything down.
        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);

        Task<ProviderSnapshot> fetchTask;
        try
        {
            // Start the fetch on a worker BEFORE racing the timeout (sol P1/P2). A `FetchAsync` runs
            // SYNCHRONOUSLY up to its first incomplete await — Codex does filesystem work there — so calling
            // it directly on the loop thread would leave that pre-await portion unbounded by the timeout.
            // Task.Run pushes the whole operation (sync prefix included) onto the thread pool so the timeout
            // below genuinely bounds all of it; a synchronous throw becomes a faulted task, observed on await.
            fetchTask = Task.Run(() => _provider.FetchAsync(fetchCts.Token), fetchCts.Token);
        }
        catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Defensive: Task.Run itself faulting synchronously is not expected (provider faults surface as a
            // faulted task, handled on await below), but degrade to n/a rather than escape the loop.
            return BuildUnavailable("fetch-error");
        }

        Task timeoutTask = DelayAsync(_fetchTimeout, timeoutCts.Token);

        Task winner = await Task.WhenAny(fetchTask, timeoutTask).ConfigureAwait(false);

        if (winner == fetchTask)
        {
            // Fetch finished first — stop and observe the pending timeout delay.
            timeoutCts.Cancel();
            Observe(timeoutTask);

            try
            {
                return await fetchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
                throw; // shutdown
            }
            catch (Exception)
            {
                return BuildUnavailable("fetch-error");
            }
        }

        // The timeout delay completed first.
        if (loopToken.IsCancellationRequested)
        {
            // It was a shutdown (the delay was cancelled by the loop token), not a fetch timeout.
            fetchCts.Cancel();
            Observe(fetchTask);
            loopToken.ThrowIfCancellationRequested();
        }

        // A genuine per-fetch timeout: stop waiting on the (possibly uncooperative) fetch and report
        // it. The orphaned fetch is cancelled and observed so it can never surface as an
        // unobserved-exception or crash the loop.
        fetchCts.Cancel();
        Observe(fetchTask);
        return BuildUnavailable("timeout");
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var wait = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        return Task.Delay(wait, _timeProvider, cancellationToken);
    }

    private ProviderSnapshot BuildUnavailable(string reasonCode) => new(
        ProviderId: _provider.Id,
        FetchedAt: _timeProvider.GetUtcNow(),
        Status: SourceStatus.Unavailable,
        StatusReasonCode: reasonCode,
        Windows: Array.Empty<UsageWindow>(),
        CreditsBalance: Metric.Unavailable<decimal>(reasonCode),
        PlanType: Metric.Unavailable<string>(reasonCode));

    private static void Observe(Task task)
    {
        // Swallow a faulted orphan's exception so it can never surface as an UnobservedTaskException.
        // Cancelled tasks carry no exception and need no observation.
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
