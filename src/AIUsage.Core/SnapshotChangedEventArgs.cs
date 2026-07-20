namespace AIUsage.Core;

/// <summary>
/// Carries the snapshot just published to the <see cref="SnapshotStore"/>. This is a PLAIN
/// event payload: the store raises <see cref="SnapshotStore.SnapshotChanged"/> on whatever thread
/// the publishing loop runs on and does NOT marshal to a UI thread — the Tray layer subscribes and
/// marshals to the WPF dispatcher itself (DESIGN.md §4; the domain core stays UI-free on purpose).
/// </summary>
public sealed class SnapshotChangedEventArgs : EventArgs
{
    /// <param name="snapshot">The newly published snapshot; never null.</param>
    public SnapshotChangedEventArgs(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    /// <summary>The snapshot that was just published (the exact instance passed to <see cref="SnapshotStore.Publish"/>).</summary>
    public ProviderSnapshot Snapshot { get; }
}
