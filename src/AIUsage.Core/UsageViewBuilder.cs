namespace AIUsage.Core;

/// <summary>
/// The "never show a wrong number" engine (DESIGN.md §5, §7; tasks T8/T17/T18). A pure, testable
/// function that maps the live <see cref="ProviderSnapshot"/>s + the retained
/// <see cref="LastKnownReadingStore"/> + the clock + a <see cref="DisplayConfig"/> into a
/// render-ready <see cref="UsageView"/> the UI draws verbatim.
/// </summary>
/// <remarks>
/// <para>
/// It reads "now" ONLY through the injected <see cref="TimeProvider"/> (never
/// <see cref="DateTimeOffset.UtcNow"/>), so every freshness / reset decision is exercisable with a
/// fake clock. It mutates nothing — recording history is the caller's job
/// (<see cref="LastKnownReadingStore.RecordFrom"/> on each publish), keeping this a side-effect-free
/// projection.
/// </para>
/// <para>
/// There is deliberately NO estimator, interpolator, projector, or carry-forward here. A window is
/// <see cref="DisplayState.Live"/> (a fresh authoritative reading), <see cref="DisplayState.Dated"/>
/// (a truthful captioned historical reading), or <see cref="DisplayState.NA"/> (an explicit
/// non-value with a reason). Nothing else can be produced.
/// </para>
/// </remarks>
public static class UsageViewBuilder
{
    /// <summary>Lowest percent a trustworthy reading may carry (inclusive).</summary>
    public const decimal MinPercent = 0m;

    /// <summary>Highest percent a trustworthy reading may carry (inclusive).</summary>
    public const decimal MaxPercent = 100m;

    /// <summary>
    /// How far into the future an observation timestamp may sit (clock skew) before it is treated as
    /// future-dated and refused LIVE (review: future-dated observations). A malformed future observation
    /// must never be accepted as "fresh".
    /// </summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(2);

    // A DELIBERATELY off provider (the kill switch / a pause) is a user choice, not an outage — it must not
    // light the tray's unknown badge (review P2-11). Mirrors NotificationDecider's benign-reason set.
    private static readonly HashSet<string> BenignOffReasons =
        new(StringComparer.Ordinal) { "disabled", "paused" };

    /// <summary>
    /// Build the whole render model from the current snapshots and retained history.
    /// </summary>
    /// <param name="snapshots">Latest snapshot per provider (e.g. <see cref="SnapshotStore.Snapshots"/>).</param>
    /// <param name="lastKnown">Retained DATED history (already updated for this cycle via <see cref="LastKnownReadingStore.RecordFrom"/>).</param>
    /// <param name="config">Owner-tunable thresholds and the Codex freshness TTL.</param>
    /// <param name="clock">The one clock the whole projection reads "now" from.</param>
    public static UsageView Build(
        IReadOnlyDictionary<string, ProviderSnapshot> snapshots,
        LastKnownReadingStore lastKnown,
        DisplayConfig config,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(lastKnown);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);

        var providers = new List<ProviderView>(snapshots.Count);
        foreach (var providerId in snapshots.Keys.OrderBy(static id => id, StringComparer.Ordinal))
        {
            providers.Add(BuildProvider(snapshots[providerId], lastKnown, config, clock));
        }

        var overallSeverity = Severity.Normal;
        foreach (var provider in providers)
        {
            overallSeverity = Higher(overallSeverity, provider.Severity);
        }

        // The icon's unknown badge rolls up ONLY over providers that are actually being monitored: a
        // deliberately-off provider (kill switch / pause) is excluded so it can't light the "?" forever and
        // train the owner to ignore the badge (review P2-11). Its card still renders "off" separately.
        var counted = new List<ProviderView>(providers.Count);
        foreach (var provider in providers)
        {
            if (!IsBenignOff(provider))
            {
                counted.Add(provider);
            }
        }

        // All-unknown iff NO monitored provider contributes a real severity signal (no LIVE window and no
        // DATED monotone-floor anywhere) — the neutral "?" state, distinct from plain-safe. No monitored
        // providers at all (startup, nothing published, or everything switched off) is likewise "cannot tell".
        var allUnknown = counted.Count == 0 || counted.All(static p => p.AllUnknown);

        // The unknown badge lights whenever any monitored region is not LIVE; all-unknown implies it.
        var unknown = allUnknown || counted.Any(static p => p.Unknown);

        return new UsageView(overallSeverity, unknown, allUnknown, providers);
    }

    private static ProviderView BuildProvider(
        ProviderSnapshot snapshot,
        LastKnownReadingStore lastKnown,
        DisplayConfig config,
        TimeProvider clock)
    {
        var windows = ClassifyWindows(snapshot, lastKnown, config, clock);

        var severity = Severity.Normal;
        var contributes = false; // a LIVE window, or a DATED monotone-floor — a real severity signal
        var anyNonLive = false;
        foreach (var window in windows)
        {
            severity = Higher(severity, window.Severity);
            if (window.DisplayState == DisplayState.Live ||
                (window.DisplayState == DisplayState.Dated && window.Severity == Severity.Warning))
            {
                contributes = true;
            }

            if (window.DisplayState != DisplayState.Live)
            {
                anyNonLive = true;
            }
        }

        var allUnknown = !contributes;
        // A provider is "unknown" if any window is not LIVE, the source itself is Unavailable, or it
        // reports no windows at all (nothing live to stand on).
        var unknown = allUnknown || anyNonLive || snapshot.Status != SourceStatus.Ok || windows.Count == 0;

        // Credits and plan are provider-level scalars, but they carry the SAME staleness risk as windows:
        // an ancient Codex event or a stale-but-Ok snapshot would otherwise show a stale balance/plan as
        // current while its windows correctly degrade (review Fix 4). Freshness-classify them with the same
        // source-status + per-metric TTL rule so a stale scalar reads n/a, never a stale current number.
        var now = clock.GetUtcNow();
        var ttl = TtlFor(snapshot.ProviderId, config);
        var sourceOk = snapshot.Status == SourceStatus.Ok;

        return new ProviderView(
            ProviderId: snapshot.ProviderId,
            FetchedAt: snapshot.FetchedAt,
            Status: snapshot.Status,
            StatusReasonCode: snapshot.StatusReasonCode,
            Windows: windows,
            CreditsBalance: ClassifyScalar(snapshot.CreditsBalance, sourceOk, ttl, now),
            PlanType: ClassifyScalar(snapshot.PlanType, sourceOk, ttl, now),
            Severity: severity,
            Unknown: unknown,
            AllUnknown: allUnknown);
    }

    /// <summary>
    /// Project a provider's windows: every window the snapshot currently reports (always shown),
    /// plus any retained DATED reading for a window the snapshot no longer reports but whose window
    /// has not reset (so history survives the provider going Unavailable). Ordered by minute-count.
    /// </summary>
    private static List<WindowView> ClassifyWindows(
        ProviderSnapshot snapshot,
        LastKnownReadingStore lastKnown,
        DisplayConfig config,
        TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var sourceOk = snapshot.Status == SourceStatus.Ok;
        var result = new List<WindowView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var window in snapshot.Windows)
        {
            seen.Add(window.Key);
            lastKnown.TryGet(snapshot.ProviderId, window.Key, out var known);
            result.Add(ClassifyWindow(snapshot.ProviderId, window.Key, window.WindowMinutes, window, sourceOk, known, config, clock));
        }

        foreach (var known in lastKnown.ForProvider(snapshot.ProviderId))
        {
            if (seen.Contains(known.Key) || now >= known.ResetsAtAtObservation)
            {
                continue; // already covered by a current window, or the retained reading's window has reset
            }

            result.Add(ClassifyWindow(snapshot.ProviderId, known.Key, known.WindowMinutes, current: null, sourceOk: false, known, config, clock));
        }

        // Display-only ordering by the authoritative minute-count (5h before weekly). Identity remains
        // WindowMinutes, never list position (DESIGN.md §3).
        result.Sort(static (a, b) => a.WindowMinutes.CompareTo(b.WindowMinutes));
        return result;
    }

    /// <summary>
    /// Classify ONE window into its <see cref="DisplayState"/> and render fields — LIVE if fresh and
    /// trustworthy; else DATED from a retained reading whose window has not reset; else NA with a
    /// reason. Public so the freshness/dated rules can be unit-tested directly.
    /// </summary>
    /// <param name="providerId">Owning provider.</param>
    /// <param name="windowKey">Composite window identity (see <see cref="UsageWindow.Key"/>) — carried onto the view for notification arming.</param>
    /// <param name="windowMinutes">Authoritative window identity.</param>
    /// <param name="current">The window in the current snapshot, or <c>null</c> if the snapshot no longer reports it.</param>
    /// <param name="sourceOk">Whether the owning snapshot's source status is Ok (LIVE requires it — DESIGN.md §5 rule 1).</param>
    /// <param name="lastKnown">A retained reading for this window, or <c>null</c>.</param>
    /// <param name="config">Thresholds + TTL.</param>
    /// <param name="clock">The clock.</param>
    public static WindowView ClassifyWindow(
        string providerId,
        string windowKey,
        int windowMinutes,
        UsageWindow? current,
        bool sourceOk,
        LastKnownReading? lastKnown,
        DisplayConfig config,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(windowKey);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        // Prefer the current window's label; for a DATED-only window (dropped from the snapshot) use the
        // label captured at observation so a scoped window keeps its identity instead of masquerading as
        // the generic "Weekly" (review P0-1).
        var label = current?.Label ?? lastKnown?.Label ?? WindowClassifier.Label(windowMinutes);
        var ttl = TtlFor(providerId, config);

        // LIVE — a fresh, in-range, unreset authoritative reading from an Ok source.
        if (sourceOk && current is not null && IsLive(current, ttl, clock))
        {
            // IsLive guarantees UsedPercent is Available, so Value is a meaningful decimal.
            var liveValue = current.UsedPercent.Value;
            return new WindowView(
                ProviderId: providerId,
                WindowMinutes: windowMinutes,
                Label: label,
                DisplayState: DisplayState.Live,
                Percent: liveValue,
                ObservedAt: current.UsedPercent.ObservedAt,
                ResetsAt: current.ResetsAt,
                Severity: SeverityFor(liveValue, config),
                ReasonCode: null,
                WindowKey: windowKey);
        }

        // DATED — truthful history sourced ONLY from the retained store, only while the window has
        // not reset. A stale current reading with no retained history is NA, not Dated.
        if (lastKnown is not null && now < lastKnown.ResetsAtAtObservation)
        {
            // Monotone-floor (DESIGN.md §5, T18): a dated reading at/above the warning threshold is a
            // hard floor on the current value, so it drives WARNING severity (paired with the unknown
            // badge upstream) — but is capped at Warning and shown as "as of T", never "≥ X%".
            var floor = lastKnown.UsedPercent >= config.WarnPercent ? Severity.Warning : Severity.Normal;

            return new WindowView(
                ProviderId: providerId,
                WindowMinutes: windowMinutes,
                Label: label,
                DisplayState: DisplayState.Dated,
                Percent: lastKnown.UsedPercent,
                ObservedAt: lastKnown.ObservedAt,
                ResetsAt: Metric.Available(lastKnown.ResetsAtAtObservation, lastKnown.ObservedAt),
                Severity: floor,
                ReasonCode: null,
                WindowKey: windowKey);
        }

        // NA — everything else, always with a reason; never a zero.
        var reason = NaReason(current, ttl, now);
        return new WindowView(
            ProviderId: providerId,
            WindowMinutes: windowMinutes,
            Label: label,
            DisplayState: DisplayState.NA,
            Percent: null,
            ObservedAt: null,
            ResetsAt: current?.ResetsAt ?? Metric.Unavailable<DateTimeOffset>(reason),
            Severity: Severity.Normal,
            ReasonCode: reason,
            WindowKey: windowKey);
    }

    /// <summary>
    /// The per-provider freshness TTL (DESIGN.md §5 LIVE rules 2 &amp; 3): Claude uses
    /// <see cref="DisplayConfig.ClaudeCurrentTtl"/> (210s), every other provider (Codex) uses
    /// <see cref="DisplayConfig.CodexCurrentTtl"/> (20 min). The two cadences are structurally different —
    /// Claude is a fixed 180s remote poll, Codex is event-driven off local files — so one TTL cannot serve both.
    /// </summary>
    public static TimeSpan TtlFor(string providerId, DisplayConfig config)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(config);
        return string.Equals(providerId, ClaudeProvider.ProviderId, StringComparison.Ordinal)
            ? config.ClaudeCurrentTtl
            : config.CodexCurrentTtl;
    }

    /// <summary>
    /// The LIVE predicate for a single window (DESIGN.md §5 LIVE rules 2 &amp; 4; tasks T8/T30). A window is
    /// live iff its <see cref="UsageWindow.UsedPercent"/> is Available, the value is in 0..100 (unrounded),
    /// the observation is within the provider's freshness TTL (<see cref="DisplayConfig.CodexCurrentTtl"/>
    /// for Codex here — see the <see cref="IsLive(UsageWindow, TimeSpan, TimeProvider)"/> overload for the
    /// per-provider TTL the builder threads in), the observation is not future-dated, and its
    /// <c>resets_at</c> is a coherent boundary still in the FUTURE. (Snapshot-status rule 1 is enforced by
    /// the caller.)
    /// </summary>
    public static bool IsLive(UsageWindow window, DisplayConfig config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        return IsLive(window, config.CodexCurrentTtl, clock);
    }

    /// <summary>
    /// The LIVE predicate with an EXPLICIT freshness TTL (DESIGN.md §5 LIVE rules 2 &amp; 4; T8/T30). This is
    /// the core the builder calls with the per-provider TTL from <see cref="TtlFor"/>. Liveness is anchored
    /// to the metric's <see cref="Metric{T}.ObservedAt"/>, so re-reading the same event does NOT renew it.
    /// A future-dated observation (beyond <see cref="MaxFutureSkew"/>) and a window whose <c>resets_at</c> is
    /// absent/unavailable or already passed are BOTH refused LIVE (review Fix 2/Fix 3).
    /// </summary>
    public static bool IsLive(UsageWindow window, TimeSpan ttl, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(clock);

        var used = window.UsedPercent;
        if (used.State != MetricState.Available || used.ObservedAt is not { } observedAt)
        {
            return false;
        }

        // State == Available guarantees Value is a meaningful decimal (State is the sole guard).
        var value = used.Value;
        if (value < MinPercent || value > MaxPercent)
        {
            return false; // out-of-range: cannot be trusted, so cannot be LIVE
        }

        var now = clock.GetUtcNow();
        var age = now - observedAt;
        if (age > ttl)
        {
            return false; // past the freshness TTL
        }

        if (age < -MaxFutureSkew)
        {
            return false; // future-dated beyond clock skew: not a real fresh reading (review: future observations)
        }

        // LIVE requires a coherent reset boundary that is still in the FUTURE (DESIGN.md §5; review Fix 3):
        // an absent/unavailable resets_at, or a reset that has already passed, means the reading cannot be
        // trusted as current — it degrades to DATED (if unreset history exists) or n/a, never LIVE.
        if (window.ResetsAt.State != MetricState.Available || now >= window.ResetsAt.Value)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Map an UNROUNDED percent to a <see cref="Severity"/> using the configured thresholds
    /// (DESIGN.md §7): ≥ crit → Critical, ≥ warn → Warning, else Normal.
    /// </summary>
    public static Severity SeverityFor(decimal percent, DisplayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (percent >= config.CritPercent)
        {
            return Severity.Critical;
        }

        if (percent >= config.WarnPercent)
        {
            return Severity.Warning;
        }

        return Severity.Normal;
    }

    private static string NaReason(UsageWindow? current, TimeSpan ttl, DateTimeOffset now)
    {
        if (current is null)
        {
            // No current window and no unreset history to show.
            return "not-reported";
        }

        // A reset boundary passed with no fresh reading → NA, NEVER zero (DESIGN.md §5).
        if (current.ResetsAt.State == MetricState.Available && now >= current.ResetsAt.Value)
        {
            return "reset-passed";
        }

        var used = current.UsedPercent;
        if (used.State == MetricState.Available)
        {
            var value = used.Value; // meaningful — State == Available
            if (value < MinPercent || value > MaxPercent)
            {
                return "out-of-range";
            }

            // A good value, but past its freshness TTL (or future-dated) with no retained history.
            if (used.ObservedAt is { } observedAt)
            {
                var age = now - observedAt;
                if (age > ttl || age < -MaxFutureSkew)
                {
                    return "no-recent-event";
                }
            }

            // Fresh, in-range value whose window's reset boundary is unknown → cannot be trusted LIVE
            // (DESIGN.md §5; review Fix 3). Distinct from "no-recent-event": the event IS recent.
            return "reset-unknown";
        }

        // NotApplicable / Unavailable carry their own source reason (e.g. "not-reported", "source-changed").
        return used.ReasonCode ?? "not-reported";
    }

    private static Severity Higher(Severity a, Severity b) => (Severity)Math.Max((int)a, (int)b);

    /// <summary>
    /// Freshness-classify a provider-level scalar (credits / plan) with the same rule the windows use
    /// (review Fix 4). An Available scalar is kept only when the source is Ok AND its own observation is
    /// within the provider TTL and not future-dated; otherwise it degrades to <c>Unavailable("stale")</c>
    /// so a stale balance/plan can never be shown as a current figure. A non-Available scalar keeps its own
    /// source reason unchanged.
    /// </summary>
    private static Metric<T> ClassifyScalar<T>(Metric<T> metric, bool sourceOk, TimeSpan ttl, DateTimeOffset now)
    {
        if (metric.State != MetricState.Available)
        {
            return metric; // already n/a — keep its own reason
        }

        if (!sourceOk)
        {
            return Metric.Unavailable<T>("stale"); // the whole source is not Ok this cycle
        }

        if (metric.ObservedAt is { } observedAt)
        {
            var age = now - observedAt;
            if (age > ttl || age < -MaxFutureSkew)
            {
                return Metric.Unavailable<T>("stale"); // past the freshness TTL, or future-dated
            }
        }

        return metric;
    }

    /// <summary>
    /// Whether a provider is DELIBERATELY off (kill switch / pause) — a user choice that must be excluded
    /// from the icon's unknown roll-up (review P2-11). Mirrors <see cref="BenignOffReasons"/>.
    /// </summary>
    private static bool IsBenignOff(ProviderView provider)
        => provider.StatusReasonCode is { } reason && BenignOffReasons.Contains(reason);
}
