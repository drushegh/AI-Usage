namespace AIUsage.Core;

/// <summary>
/// The one place in the domain core that reads "now", and it does so exclusively through
/// an injected <see cref="TimeProvider"/> — never <see cref="DateTimeOffset.UtcNow"/> directly
/// (dotnet-development standard; DESIGN.md §5). This keeps all freshness logic testable with a
/// fake clock. It is the seam the later LIVE/TTL rules (DESIGN.md §5 — tasks T8/T30) build on;
/// here it provides only the primitive: the age of an available observation.
/// </summary>
public static class MetricFreshness
{
    /// <summary>
    /// Age of an <see cref="MetricState.Available"/> metric's observation relative to
    /// <paramref name="timeProvider"/>'s clock. Returns <c>null</c> for any non-Available metric —
    /// a metric with no authoritative observation has no meaningful age (and must not be treated as fresh).
    /// The result may be negative under clock skew; callers layering freshness policy handle that (DESIGN.md §5).
    /// </summary>
    public static TimeSpan? Age<T>(Metric<T> metric, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (metric.State != MetricState.Available || metric.ObservedAt is not { } observedAt)
        {
            return null;
        }

        return timeProvider.GetUtcNow() - observedAt;
    }
}
