namespace AIUsage.Core.Tests;

/// <summary>
/// Records every <see cref="SnapshotStore.SnapshotChanged"/> event and lets a test await an exact
/// condition over the recorded sequence. Tests assert on real outcomes (the awaited condition
/// actually holds) rather than sleeping-then-checking, so they are deterministic; a broken
/// implementation is caught by the caller's <see cref="TaskExtensions.WaitAsync"/> safety timeout,
/// not by a race.
/// </summary>
internal sealed class SnapshotWatcher : IDisposable
{
    private readonly SnapshotStore _store;
    private readonly object _gate = new();
    private readonly List<ProviderSnapshot> _events = new();
    private readonly List<(Func<IReadOnlyList<ProviderSnapshot>, bool> Condition, TaskCompletionSource Tcs)> _waiters = new();

    public SnapshotWatcher(SnapshotStore store)
    {
        _store = store;
        _store.SnapshotChanged += OnChanged;
    }

    /// <summary>A point-in-time copy of every snapshot recorded so far, in publish order.</summary>
    public IReadOnlyList<ProviderSnapshot> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>
    /// Complete when <paramref name="condition"/> first holds over the recorded sequence (checked
    /// immediately and after every subsequent publish).
    /// </summary>
    public Task WaitAsync(Func<IReadOnlyList<ProviderSnapshot>, bool> condition)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (condition(_events))
            {
                return Task.CompletedTask;
            }

            _waiters.Add((condition, tcs));
        }

        return tcs.Task;
    }

    /// <summary>Count recorded snapshots matching <paramref name="predicate"/>.</summary>
    public int CountMatching(Func<ProviderSnapshot, bool> predicate)
    {
        lock (_gate)
        {
            return _events.Count(predicate);
        }
    }

    private void OnChanged(object? sender, SnapshotChangedEventArgs e)
    {
        List<TaskCompletionSource> ready = new();
        lock (_gate)
        {
            _events.Add(e.Snapshot);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Condition(_events))
                {
                    ready.Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var tcs in ready)
        {
            tcs.TrySetResult();
        }
    }

    public void Dispose() => _store.SnapshotChanged -= OnChanged;
}
