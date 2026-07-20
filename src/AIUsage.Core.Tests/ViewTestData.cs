namespace AIUsage.Core.Tests;

/// <summary>
/// Constructors for the display-model tests (T8/T17/T18): build <see cref="ProviderSnapshot"/>s and
/// window maps from constructed data + a fake clock — never from the real Codex/Claude data paths
/// (that is T7's job). Keeps the accuracy-engine tests fully deterministic.
/// </summary>
internal static class ViewTestData
{
    public const string Codex = "codex";
    public const string Claude = "claude";

    /// <summary>A fixed, timezone-explicit base "now" far from wall-clock time so no hidden UtcNow leaks in.</summary>
    public static readonly DateTimeOffset Base = new(2026, 07, 19, 14, 00, 00, TimeSpan.Zero);

    /// <summary>A window whose used-percent and reset time are both Available (the normal reported shape).</summary>
    public static UsageWindow Window(int minutes, decimal percent, DateTimeOffset observedAt, DateTimeOffset resetsAt)
        => new(
            WindowMinutes: minutes,
            Label: WindowClassifier.Label(minutes),
            UsedPercent: Metric.Available(percent, observedAt),
            ResetsAt: Metric.Available(resetsAt, observedAt));

    /// <summary>An Ok snapshot carrying the given windows (credits/plan default to not-reported).</summary>
    public static ProviderSnapshot OkSnapshot(string providerId, DateTimeOffset fetchedAt, params UsageWindow[] windows)
        => new(
            ProviderId: providerId,
            FetchedAt: fetchedAt,
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: windows,
            CreditsBalance: Metric.NotApplicable<decimal>("not-reported"),
            PlanType: Metric.NotApplicable<string>("not-reported"));

    /// <summary>An Unavailable snapshot with no windows (a provider card of honest n/a — DESIGN.md §3).</summary>
    public static ProviderSnapshot UnavailableSnapshot(string providerId, DateTimeOffset fetchedAt, string reason)
        => new(
            ProviderId: providerId,
            FetchedAt: fetchedAt,
            Status: SourceStatus.Unavailable,
            StatusReasonCode: reason,
            Windows: Array.Empty<UsageWindow>(),
            CreditsBalance: Metric.Unavailable<decimal>(reason),
            PlanType: Metric.Unavailable<string>(reason));

    public static IReadOnlyDictionary<string, ProviderSnapshot> Map(params ProviderSnapshot[] snapshots)
    {
        var map = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            map[snapshot.ProviderId] = snapshot;
        }

        return map;
    }

    /// <summary>Pull the single <see cref="WindowView"/> for a given provider + window minute-count.</summary>
    public static WindowView Window(this UsageView view, string providerId, int windowMinutes)
        => view.Providers.Single(p => p.ProviderId == providerId).Windows.Single(w => w.WindowMinutes == windowMinutes);
}
