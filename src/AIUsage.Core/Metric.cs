namespace AIUsage.Core;

/// <summary>
/// A single tri-state figure. The type makes illegal states unconstructable:
/// the constructor is <c>private</c>, so the <em>only</em> way to obtain a
/// <see cref="Metric{T}"/> is through the invariant-enforcing factories on the
/// non-generic <see cref="Metric"/> helper. This deviates deliberately from the
/// positional-record sketch in DESIGN.md §3 (a public positional constructor would
/// let a caller build an <see cref="MetricState.Available"/> metric with no value, or
/// attach a reason to an available one) — the enforced-factory shape is the point.
/// </summary>
/// <remarks>
/// Invariants (guaranteed by construction):
/// <list type="bullet">
///   <item><see cref="Value"/> and <see cref="ObservedAt"/> are present iff <see cref="State"/> is <see cref="MetricState.Available"/>.</item>
///   <item><see cref="ReasonCode"/> is present iff <see cref="State"/> is NOT <see cref="MetricState.Available"/>.</item>
///   <item>All members are get-only, so a <c>with</c> expression cannot mutate a metric into an illegal state.</item>
/// </list>
/// <see cref="ObservedAt"/> is PER-METRIC (DESIGN.md §3) — sibling metrics observe and
/// expire independently; one snapshot-level timestamp cannot express that.
/// For a value-type <typeparamref name="T"/> (e.g. <c>decimal</c>) a non-available metric's
/// <see cref="Value"/> is the CLR default (<c>0</c>) rather than null — it is meaningless and
/// must never be read; <see cref="State"/> is the sole guard for whether <see cref="Value"/> is real.
/// </remarks>
/// <typeparam name="T">The value's type when available (e.g. <c>decimal</c>, <c>DateTimeOffset</c>, <c>string</c>).</typeparam>
public sealed record Metric<T>
{
    /// <summary>Which of the three states this metric is in.</summary>
    public MetricState State { get; }

    /// <summary>The observed value; meaningful iff <see cref="State"/> is <see cref="MetricState.Available"/>.</summary>
    public T? Value { get; }

    /// <summary>When the value was observed; present iff <see cref="State"/> is <see cref="MetricState.Available"/>.</summary>
    public DateTimeOffset? ObservedAt { get; }

    /// <summary>Machine-readable reason; present iff <see cref="State"/> is NOT <see cref="MetricState.Available"/>.</summary>
    public string? ReasonCode { get; }

    private Metric(MetricState state, T? value, DateTimeOffset? observedAt, string? reasonCode)
    {
        State = state;
        Value = value;
        ObservedAt = observedAt;
        ReasonCode = reasonCode;
    }

    // The ONLY construction paths. Kept internal so the non-generic Metric helper is the
    // public API surface; the private ctor means even in-assembly code cannot bypass these.

    internal static Metric<T> CreateAvailable(T value, DateTimeOffset observedAt)
    {
        // `value is null` is a no-op for value-type T and a genuine guard for reference-type T.
        // A real zero (e.g. 0% used) is a legal Available value; only a MISSING value is rejected.
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "An Available metric requires a value.");
        }

        return new Metric<T>(MetricState.Available, value, observedAt, reasonCode: null);
    }

    internal static Metric<T> CreateNotApplicable(string reasonCode)
        => new(MetricState.NotApplicable, default, observedAt: null, RequireReason(reasonCode));

    internal static Metric<T> CreateUnavailable(string reasonCode)
        => new(MetricState.Unavailable, default, observedAt: null, RequireReason(reasonCode));

    private static string RequireReason(string reasonCode)
        => string.IsNullOrWhiteSpace(reasonCode)
            ? throw new ArgumentException("A non-Available metric requires a reason code.", nameof(reasonCode))
            : reasonCode;
}

/// <summary>
/// Factory surface for <see cref="Metric{T}"/>. Non-generic so calls read
/// <c>Metric.Available(value, observedAt)</c> with <typeparamref name="T"/> inferred,
/// while <c>Metric.NotApplicable&lt;T&gt;(reason)</c> / <c>Metric.Unavailable&lt;T&gt;(reason)</c>
/// name the metric's value type explicitly.
/// </summary>
public static class Metric
{
    /// <summary>
    /// Build an <see cref="MetricState.Available"/> metric. Both <paramref name="value"/> and
    /// <paramref name="observedAt"/> are mandatory — there is no path to an available metric without them.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null (reference-type <typeparamref name="T"/>).</exception>
    public static Metric<T> Available<T>(T value, DateTimeOffset observedAt)
        => Metric<T>.CreateAvailable(value, observedAt);

    /// <summary>Build a <see cref="MetricState.NotApplicable"/> metric carrying <paramref name="reasonCode"/> (e.g. "not-reported").</summary>
    /// <exception cref="ArgumentException"><paramref name="reasonCode"/> is null or blank.</exception>
    public static Metric<T> NotApplicable<T>(string reasonCode)
        => Metric<T>.CreateNotApplicable(reasonCode);

    /// <summary>Build an <see cref="MetricState.Unavailable"/> metric carrying <paramref name="reasonCode"/> (e.g. "throttled", "source-changed").</summary>
    /// <exception cref="ArgumentException"><paramref name="reasonCode"/> is null or blank.</exception>
    public static Metric<T> Unavailable<T>(string reasonCode)
        => Metric<T>.CreateUnavailable(reasonCode);
}
