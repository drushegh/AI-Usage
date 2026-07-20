using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AIUsage.Core;

/// <summary>
/// The retained history that feeds ONLY the DATED "Last known" area (DESIGN.md §3, §5; task T17).
/// Keyed by <c>(providerId, windowKey)</c> — the COMPOSITE window identity (see <see cref="UsageWindow.Key"/>),
/// NOT the bare minute-count — so Claude's two same-minute weekly windows retain independent history
/// (review P0-1). It holds the last GOOD reading per window,
/// independently of the live <see cref="SnapshotStore"/> — so replacing a snapshot (or a provider
/// going Unavailable) never destroys true history. It is a plain retention store: it decides
/// nothing about display. Whether a retained reading is still shown is a read-time decision made by
/// <see cref="UsageViewBuilder"/> (shown only while <c>now &lt; ResetsAtAtObservation</c>).
/// </summary>
/// <remarks>
/// This is history, not an estimator: it stores exactly what was observed, never a projection.
/// Thread-safe for concurrent provider loops and UI readers, mirroring <see cref="SnapshotStore"/>.
/// </remarks>
public sealed class LastKnownReadingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string ProviderId, string WindowKey), LastKnownReading> _byKey = new();

    /// <summary>
    /// Record (upsert) a last-known reading for its <c>(ProviderId, Key)</c> — the composite window
    /// identity (see <see cref="LastKnownReading.Key"/>). A strictly OLDER observation never clobbers a
    /// newer one — the newest observation wins — so an out-of-order publish (a stale snapshot arriving
    /// late) cannot rewind history.
    /// </summary>
    public void Record(LastKnownReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var key = (reading.ProviderId, reading.Key);
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existing) && existing.ObservedAt > reading.ObservedAt)
            {
                return; // keep the newer observation
            }

            _byKey[key] = reading;
        }
    }

    /// <summary>
    /// Record every window in <paramref name="snapshot"/> that yields a GOOD reading — an Available,
    /// in-range (0..100) <see cref="UsageWindow.UsedPercent"/> with a coherent Available
    /// <see cref="UsageWindow.ResetsAt"/>. The reset time is mandatory: without it a DATED reading
    /// could never be suppressed once its window rolls over, so such windows are deliberately not
    /// retained (DESIGN.md §5 — DATED shows only "while that reading's window has not reset").
    /// Call this on each publish before building the view.
    /// </summary>
    public void RecordFrom(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var window in snapshot.Windows)
        {
            // State == Available is the sole guard that Value/ObservedAt are meaningful (for value-type
            // T, Metric<T>.Value is a non-nullable T, so read it only behind the state check).
            if (window.UsedPercent.State == MetricState.Available &&
                window.UsedPercent.ObservedAt is { } observedAt &&
                window.ResetsAt.State == MetricState.Available)
            {
                var percent = window.UsedPercent.Value;
                if (percent is < 0m or > 100m)
                {
                    continue; // out-of-range garbage never enters history
                }

                Record(new LastKnownReading(
                    snapshot.ProviderId, window.WindowMinutes, percent, observedAt, window.ResetsAt.Value, window.Key, window.Label));
            }
        }
    }

    /// <summary>
    /// Get the retained reading for <paramref name="providerId"/> under the composite
    /// <paramref name="windowKey"/> (see <see cref="UsageWindow.Key"/>), if any.
    /// </summary>
    public bool TryGet(string providerId, string windowKey, [NotNullWhen(true)] out LastKnownReading? reading)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(windowKey);
        lock (_gate)
        {
            return _byKey.TryGetValue((providerId, windowKey), out reading);
        }
    }

    /// <summary>
    /// Get the retained reading for <paramref name="providerId"/>/<paramref name="windowMinutes"/>, if any —
    /// a convenience overload that keys by the bare minute-count (the composite key of a NON-scoped window).
    /// </summary>
    public bool TryGet(string providerId, int windowMinutes, [NotNullWhen(true)] out LastKnownReading? reading)
        => TryGet(providerId, windowMinutes.ToString(CultureInfo.InvariantCulture), out reading);

    /// <summary>
    /// A point-in-time copy of every retained reading for <paramref name="providerId"/> (any order).
    /// Used to surface DATED history for a window even when the current snapshot no longer reports it
    /// (e.g. the provider is Unavailable) — the reading's own reset time bounds its validity.
    /// </summary>
    public IReadOnlyList<LastKnownReading> ForProvider(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        lock (_gate)
        {
            var result = new List<LastKnownReading>();
            foreach (var entry in _byKey)
            {
                if (string.Equals(entry.Key.ProviderId, providerId, StringComparison.Ordinal))
                {
                    result.Add(entry.Value);
                }
            }

            return result;
        }
    }
}
