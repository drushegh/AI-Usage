using System.Diagnostics.CodeAnalysis;

namespace AIUsage.Core;

/// <summary>
/// Holds the latest immutable <see cref="ProviderSnapshot"/> per <see cref="ProviderSnapshot.ProviderId"/>
/// (DESIGN.md §4). Provider loops publish into it; the UI renders only from it. Thread-safe for
/// concurrent publishers and readers.
/// </summary>
/// <remarks>
/// Two invariants matter for the accuracy contract:
/// <list type="bullet">
///   <item><b>Startup = n/a.</b> Before a provider's first <see cref="Publish"/>, reads return
///   nothing — <see cref="TryGet"/> is <c>false</c> and <see cref="Snapshots"/> omits the key.
///   The store never fabricates a stale or zero-valued placeholder; "no observation yet" is the
///   honest state the UI renders as n/a (DESIGN.md §3/§5).</item>
///   <item><b>Atomic, newest-wins per provider.</b> Each publish swaps one provider's slot
///   under a lock, so a reader never sees a half-updated map. A publish whose
///   <see cref="ProviderSnapshot.FetchedAt"/> PRECEDES the currently-stored reading for that provider is
///   DROPPED (P2-15): a manual refresh racing the runner loop can otherwise publish an older snapshot over
///   a newer one, stepping the displayed reading and footer age backwards. Equal-or-newer wins.</item>
/// </list>
/// The <see cref="SnapshotChanged"/> event is raised OUTSIDE the lock (subscriber code must never
/// run while the store's lock is held) and carries the exact published snapshot.
/// </remarks>
public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderSnapshot> _byProviderId = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised after each <see cref="Publish"/>, carrying the new snapshot. Plain event — NOT
    /// marshalled to any UI thread; consumers marshal themselves (DESIGN.md §4).
    /// </summary>
    public event EventHandler<SnapshotChangedEventArgs>? SnapshotChanged;

    /// <summary>
    /// Get the latest snapshot for <paramref name="providerId"/>. Returns <c>false</c> with a null
    /// <paramref name="snapshot"/> when the provider has never published — the startup "n/a" state.
    /// </summary>
    public bool TryGet(string providerId, [NotNullWhen(true)] out ProviderSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        lock (_gate)
        {
            return _byProviderId.TryGetValue(providerId, out snapshot);
        }
    }

    /// <summary>
    /// An atomic point-in-time view of every provider's latest snapshot. The returned dictionary is
    /// an immutable copy — a snapshot published after this call never mutates a view already handed out.
    /// </summary>
    public IReadOnlyDictionary<string, ProviderSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, ProviderSnapshot>(_byProviderId, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Publish <paramref name="snapshot"/> as the latest reading for its provider (atomic swap,
    /// newest-wins), then raise <see cref="SnapshotChanged"/> with it. Safe to call concurrently from
    /// multiple provider loops. A snapshot older than the one already stored for that provider (by
    /// <see cref="ProviderSnapshot.FetchedAt"/>) is DROPPED — it is neither stored nor announced — so a
    /// racing manual refresh can never rewind a newer reading (P2-15).
    /// </summary>
    public void Publish(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_byProviderId.TryGetValue(snapshot.ProviderId, out var existing) &&
                snapshot.FetchedAt < existing.FetchedAt)
            {
                // A newer reading is already stored for this provider — dropping the stale one keeps the
                // displayed reading and footer age monotonic. Nothing changed, so do not raise the event.
                return;
            }

            _byProviderId[snapshot.ProviderId] = snapshot;
        }

        // Raised outside the lock: subscriber code (e.g. a UI marshal) must never execute while the
        // store lock is held, or a re-entrant read would deadlock.
        SnapshotChanged?.Invoke(this, new SnapshotChangedEventArgs(snapshot));
    }
}
