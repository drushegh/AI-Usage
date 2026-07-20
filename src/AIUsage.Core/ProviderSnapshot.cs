namespace AIUsage.Core;

/// <summary>
/// An immutable, whole-source reading published by a provider loop into the store
/// (DESIGN.md §3, §4). The UI renders only from these. Windows that appear or disappear
/// between snapshots are a non-event; each metric degrades to its own non-Available
/// state independently and never poisons a sibling.
/// </summary>
/// <param name="ProviderId">Stable provider identity, e.g. "claude" or "codex".</param>
/// <param name="FetchedAt">When this snapshot was produced by the loop.</param>
/// <param name="Status">Whole-source status; a provider card renders even when this is <see cref="SourceStatus.Unavailable"/>.</param>
/// <param name="StatusReasonCode">Reason for a non-Ok <paramref name="Status"/>; null when <see cref="SourceStatus.Ok"/>.</param>
/// <param name="Windows">Whatever windows the source currently reports (may be empty).</param>
/// <param name="CreditsBalance">Provider credit balance as its own independent metric.</param>
/// <param name="PlanType">Provider plan/tier as its own independent metric.</param>
public sealed record ProviderSnapshot(
    string ProviderId,
    DateTimeOffset FetchedAt,
    SourceStatus Status,
    string? StatusReasonCode,
    IReadOnlyList<UsageWindow> Windows,
    Metric<decimal> CreditsBalance,
    Metric<string> PlanType);
