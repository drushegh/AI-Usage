using System.Globalization;

namespace AIUsage.Core;

/// <summary>
/// The pure notification engine (DESIGN.md §7 Notifications; tasks T36/T37). Fed a fresh
/// <see cref="UsageView"/> plus "now" on every tick, it returns the list of notifications to fire
/// this tick — deciding WHAT should toast, never HOW (the tray turns each request into a balloon).
/// It reads "now" only from the caller-supplied timestamp (never <see cref="DateTimeOffset.UtcNow"/>),
/// so the dwell/flap timing is fully exercisable with a fake clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateful across ticks, single-threaded by contract.</b> It remembers per-window threshold
/// arming and per-provider transition state between calls. The tray drives it from the one WPF
/// dispatcher thread (App builds a <see cref="UsageView"/> per <see cref="SnapshotChangedEventArgs"/>
/// on that thread), so no locking is needed — do not call <see cref="Evaluate"/> concurrently.
/// </para>
/// <para>
/// <b>Threshold (T37).</b> A window that is <see cref="DisplayState.Live"/> and crosses UP through the
/// configured warn/crit threshold (compared UNROUNDED, shared with the icon severity config) fires
/// exactly one notification, and does not re-fire until that window RESETS. Arming escalates
/// Normal → Warning → Critical within a window period; a dip without a reset never re-arms. The very
/// first observation of a window establishes a baseline silently — a crossing must be witnessed, so a
/// window already above threshold when first seen (e.g. right after an app restart) does not toast.
/// DATED / N-A readings never contribute (they carry no LIVE number).
/// </para>
/// <para>
/// <b>Transition (T36).</b> A provider that goes Ok → Unavailable and STAYS Unavailable for longer than
/// <see cref="UnavailableDwell"/> fires one toast naming the provider + reason. Damped: a first flicker
/// never toasts (a recovery before the dwell clears the streak). Flap-suppressed: no repeat for the same
/// (provider, reason) within <see cref="FlapSuppression"/>. A recovery (→ Ok) re-arms the streak. Benign
/// opt-out reasons ("disabled", "paused") never toast — they are user choices, not outages.
/// </para>
/// </remarks>
public sealed class NotificationDecider
{
    /// <summary>How long an Ok → Unavailable streak must persist before it toasts (DESIGN.md §7 "> 5 min").</summary>
    public static readonly TimeSpan UnavailableDwell = TimeSpan.FromMinutes(5);

    /// <summary>No repeat transition toast for the same (provider, reason) within this window (DESIGN.md §7 "30 min").</summary>
    public static readonly TimeSpan FlapSuppression = TimeSpan.FromMinutes(30);

    // Deliberate off-states are user choices, not outages — they must never raise a transition toast.
    private static readonly HashSet<string> BenignUnavailableReasons =
        new(StringComparer.Ordinal) { "disabled", "paused" };

    private readonly DisplayConfig _config;
    // Arming is keyed by the COMPOSITE window identity (WindowView.Key), NOT the bare minute-count, so
    // Claude's two same-minute weekly windows arm independently (review P0-1) — otherwise the scoped window
    // reads as a first-observation "UP crossing" of the shared arm and toasts spuriously, and a later
    // weekly_all crossing is suppressed by the scoped window's earlier announcement.
    private readonly Dictionary<(string Provider, string WindowKey), WindowArm> _thresholds = new();
    private readonly Dictionary<string, ProviderStreak> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Provider, string Reason), DateTimeOffset> _lastTransitionFire = new();

    /// <param name="config">The thresholds to compare against — the SAME config the icon severity uses.</param>
    public NotificationDecider(DisplayConfig config)
        => _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>
    /// Evaluate one tick and return every notification to fire now (usually empty). Pure with respect to
    /// the outside world (no I/O, no wall clock) but advances the decider's internal arming state.
    /// </summary>
    /// <param name="view">The render model just built for this tick.</param>
    /// <param name="now">The tick's timestamp, from the caller's <see cref="TimeProvider"/>.</param>
    public IReadOnlyList<NotificationRequest> Evaluate(UsageView view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);

        var results = new List<NotificationRequest>();
        foreach (var provider in view.Providers)
        {
            EvaluateThresholds(provider, now, results);
            EvaluateTransition(provider, now, results);
        }

        return results;
    }

    // ---- Threshold crossings (T37) --------------------------------------------------------------

    private void EvaluateThresholds(ProviderView provider, DateTimeOffset now, List<NotificationRequest> results)
    {
        foreach (var window in provider.Windows)
        {
            // Only a LIVE reading carries a trustworthy current number (DESIGN.md §5) — DATED history and
            // the monotone-floor never toast (re-asserts T17/T18), and n/a carries no value at all.
            if (window.DisplayState != DisplayState.Live || window.Percent is not { } percent)
            {
                continue;
            }

            var key = (provider.ProviderId, window.Key);
            if (!_thresholds.TryGetValue(key, out var arm))
            {
                arm = new WindowArm();
                _thresholds[key] = arm;
            }

            // A window RESET re-arms (DESIGN.md §7): resets_at advancing to a strictly later boundary is a
            // new window period, so the highest-notified level drops back to Normal and a fresh baseline is
            // taken. resets_at is the authoritative reset signal (§5).
            var resetsAt = window.ResetsAt.State == MetricState.Available ? window.ResetsAt.Value : (DateTimeOffset?)null;
            if (resetsAt is { } boundary && arm.PeriodResetsAt is { } previousBoundary && boundary > previousBoundary)
            {
                arm.Seen = false;
                arm.NotifiedLevel = Severity.Normal;
            }

            if (resetsAt is { } known)
            {
                arm.PeriodResetsAt = known;
            }

            var band = UsageViewBuilder.SeverityFor(percent, _config);

            if (!arm.Seen)
            {
                // First reading this window period: establish the baseline, never toast. A crossing must be
                // witnessed as an UP transition — a window already hot when first observed is not "news".
                arm.Seen = true;
                arm.NotifiedLevel = band;
                continue;
            }

            if (band > arm.NotifiedLevel)
            {
                // Crossed UP into a higher band than we have already announced this period → one toast.
                arm.NotifiedLevel = band;
                results.Add(ThresholdRequest(provider.ProviderId, window, percent, band, now));
            }
        }
    }

    private static NotificationRequest ThresholdRequest(
        string providerId, WindowView window, decimal percent, Severity band, DateTimeOffset now)
    {
        var title = $"{ProviderName(providerId)} {window.Label} at {FormatPercent(percent)}%";

        string body;
        if (window.ResetsAt.State == MetricState.Available && window.ResetsAt.Value > now)
        {
            var countdown = DurationFormat.Compact(window.ResetsAt.Value - now);
            body = countdown.Length > 0 ? $"Resets in {countdown}." : "Threshold crossed.";
        }
        else
        {
            body = "Threshold crossed.";
        }

        return new NotificationRequest(NotificationKind.Threshold, title, body, band);
    }

    // ---- Provider transitions (T36) -------------------------------------------------------------

    private void EvaluateTransition(ProviderView provider, DateTimeOffset now, List<NotificationRequest> results)
    {
        if (!_providers.TryGetValue(provider.ProviderId, out var streak))
        {
            streak = new ProviderStreak();
            _providers[provider.ProviderId] = streak;
        }

        if (provider.Status == SourceStatus.Ok)
        {
            // Recovery re-arms: the next Ok → Unavailable transition starts a brand-new streak.
            streak.PrevStatus = SourceStatus.Ok;
            streak.UnavailableSince = null;
            streak.FiredReason = null;
            return;
        }

        // provider.Status == Unavailable.
        var reason = string.IsNullOrEmpty(provider.StatusReasonCode) ? "unavailable" : provider.StatusReasonCode;

        // Only a genuine Ok → Unavailable transition arms a dwell. A provider that has been Unavailable
        // since the very first observation (never seen Ok) is "down from the start", not a transition.
        if (streak.PrevStatus == SourceStatus.Ok)
        {
            streak.UnavailableSince = now;
            streak.FiredReason = null;
        }

        streak.PrevStatus = SourceStatus.Unavailable;

        if (streak.UnavailableSince is not { } since)
        {
            return; // not armed (down from start) — never toasts
        }

        // Damping falls out of the dwell: a flicker that recovers before UnavailableDwell clears the streak
        // on the Ok tick above, so it never reaches here.
        if (now - since <= UnavailableDwell)
        {
            return;
        }

        if (BenignUnavailableReasons.Contains(reason))
        {
            return; // deliberate opt-out, not an outage
        }

        if (string.Equals(streak.FiredReason, reason, StringComparison.Ordinal))
        {
            return; // already toasted this reason for this streak (sustained → exactly one toast)
        }

        // Flap suppression: no repeat for the same (provider, reason) within FlapSuppression, even across
        // recover/re-fail cycles. A DIFFERENT reason is not blocked by a prior reason's fire.
        var flapKey = (provider.ProviderId, reason);
        if (_lastTransitionFire.TryGetValue(flapKey, out var lastFired) && now - lastFired < FlapSuppression)
        {
            return;
        }

        _lastTransitionFire[flapKey] = now;
        streak.FiredReason = reason;
        results.Add(TransitionRequest(provider.ProviderId, reason));
    }

    private static NotificationRequest TransitionRequest(string providerId, string reason)
    {
        var name = ProviderName(providerId);
        var title = $"{name} unavailable";
        var body = $"{name} has been unavailable for over 5 minutes ({Humanize(reason)}).";
        return new NotificationRequest(NotificationKind.Transition, title, body, Severity.Warning);
    }

    // ---- Shared formatting ----------------------------------------------------------------------

    private static string ProviderName(string providerId) => providerId switch
    {
        "codex" => "Codex",
        "claude" => "Claude",
        _ => Capitalise(providerId),
    };

    private static string Capitalise(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    // A reason code humanised for prose: "auth-rejected" → "auth rejected". Deliberately does NOT
    // duplicate the tray's fuller FriendlyReason vocabulary — a toast just needs it readable, never blank.
    private static string Humanize(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "unavailable" : reason.Replace('-', ' ');

    // One-decimal display, trailing ".0" trimmed: 90 → "90", 90.45 → "90.5" (matches the tray's Percent rule).
    private static string FormatPercent(decimal value)
        => Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Per-window threshold arming — the highest band announced in the current window period.</summary>
    private sealed class WindowArm
    {
        public bool Seen;
        public Severity NotifiedLevel;
        public DateTimeOffset? PeriodResetsAt;
    }

    /// <summary>Per-provider transition streak state.</summary>
    private sealed class ProviderStreak
    {
        public SourceStatus? PrevStatus;
        public DateTimeOffset? UnavailableSince;
        public string? FiredReason;
    }
}
