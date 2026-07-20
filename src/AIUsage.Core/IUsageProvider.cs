namespace AIUsage.Core;

/// <summary>
/// A single usage source (e.g. Codex, Claude). Each provider is an isolated async loop that
/// produces an immutable <see cref="ProviderSnapshot"/> per fetch (DESIGN.md §4). Providers share
/// nothing but the <see cref="SnapshotStore"/>; one provider's outage, hang, or parse failure can
/// never delay or degrade another. A third provider is a new compile-time adapter plus one
/// registration line on <see cref="ProviderHost"/> — never a plugin loaded at runtime (DESIGN.md §3).
/// </summary>
public interface IUsageProvider
{
    /// <summary>Stable provider identity, e.g. "codex" or "claude". Used as the store key.</summary>
    string Id { get; }

    /// <summary>
    /// The minimum time the loop waits BETWEEN fetches — the cadence floor
    /// (e.g. Codex's reconciliation poll, Claude's 180s hard gate; DESIGN.md §4.1/§4.2).
    /// The per-fetch timeout is a separate, shorter bound applied by <see cref="ProviderRunner"/>.
    /// </summary>
    TimeSpan MinInterval { get; }

    /// <summary>
    /// Perform one fetch and return an immutable snapshot. Implementations SHOULD honour
    /// <paramref name="cancellationToken"/>, but correctness does not depend on it: the runner
    /// bounds every fetch with its own timeout and converts any fault OR timeout into an
    /// <see cref="SourceStatus.Unavailable"/> snapshot, so a fetch that throws or never returns
    /// still cannot crash the loop (DESIGN.md §4).
    /// </summary>
    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}
