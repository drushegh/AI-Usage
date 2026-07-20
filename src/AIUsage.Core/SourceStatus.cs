namespace AIUsage.Core;

/// <summary>
/// Snapshot-level status of a provider source (DESIGN.md §3). A provider card never
/// disappears on failure — an <see cref="Unavailable"/> snapshot is still rendered,
/// with its per-metric reasons intact.
/// </summary>
public enum SourceStatus
{
    /// <summary>The source responded and produced a usable snapshot (individual metrics may still be non-Available).</summary>
    Ok,

    /// <summary>The source as a whole could not be read this cycle (carries a status reason code).</summary>
    Unavailable,
}
