using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace AIUsage.Core;

/// <summary>
/// The pure, I/O-FREE core of the Codex collector (DESIGN.md §4.1; spike <c>e3-findings.md</c>).
/// It turns bounded session-file <em>tails</em> (already read from disk by <see cref="CodexProvider"/>,
/// or fed directly by a test) into a <see cref="ProviderSnapshot"/>, with no filesystem, clock, or
/// network access of its own — every "now" comes in as the <c>fetchedAt</c> argument, every observation
/// time comes from the event's embedded UTC <c>.timestamp</c>. Keeping this the highest-risk logic
/// (real file-format parsing) as a pure function is what makes it exhaustively testable against the
/// E3 fixtures.
/// </summary>
/// <remarks>
/// The algorithm is the E3-verified "6-step" collector:
/// <list type="number">
///   <item>Parse each tail LINE-BY-LINE with try/catch — tolerate a leading fragment (a 64 KB tail
///   begins mid-line) and a half-written trailing line; a single bad line never aborts the read.</item>
///   <item>Keep only lines that are <c>type=="event_msg"</c> AND <c>payload.type=="token_count"</c>
///   AND carry a valid embedded <c>.timestamp</c> AND a usable non-null <c>primary</c> window
///   (this is what skips the <c>primary:null</c> degenerate tail).</item>
///   <item>Across ALL candidate tails, select the event with the greatest embedded <c>.timestamp</c>
///   — NOT the last line, NOT the newest file (mtime reorders under concurrent sessions).</item>
///   <item>Classify each present window by <c>window_minutes</c> via <see cref="WindowClassifier"/>,
///   never by <c>primary</c>/<c>secondary</c> position.</item>
///   <item>No usable event anywhere → <see cref="SourceStatus.Unavailable"/> <c>"no-recent-event"</c>.</item>
/// </list>
/// </remarks>
public static class CodexSessionParser
{
    /// <summary>The <c>event_msg</c> value that wraps a rate-limit event (e3-findings §1 — NOT top-level <c>token_count</c>).</summary>
    private const string EventMsgType = "event_msg";

    /// <summary>The <c>payload.type</c> value that identifies a rate-limit event.</summary>
    private const string TokenCountPayloadType = "token_count";

    /// <summary>
    /// How far ahead of the fetch time an embedded observation timestamp may legitimately sit (clock skew)
    /// before it is treated as a future-dated forgery and quarantined from selection (review: future-dated
    /// observations). A malformed future event must never eclipse valid past events and sit LIVE forever.
    /// </summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Parse and map a set of candidate session-file tails into the current Codex snapshot.
    /// Collects every usable <c>token_count</c> event across every tail, selects the one with the
    /// greatest embedded <c>.timestamp</c>, and maps it; if none is usable, returns
    /// <see cref="SourceStatus.Unavailable"/> with reason <c>"no-recent-event"</c>.
    /// </summary>
    /// <param name="candidateTails">Bounded tails of the candidate files (nulls are skipped). Order is irrelevant — selection is by embedded timestamp.</param>
    /// <param name="fetchedAt">When this fetch ran (the snapshot's <see cref="ProviderSnapshot.FetchedAt"/>); NOT an observation time.</param>
    public static ProviderSnapshot BuildSnapshot(IEnumerable<string> candidateTails, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(candidateTails);

        CodexUsageEvent? newest = null;
        foreach (var tail in candidateTails)
        {
            if (tail is null)
            {
                continue;
            }

            foreach (var evt in ParseUsableEvents(tail))
            {
                // Quarantine a future-dated observation: an embedded timestamp later than the fetch time
                // (beyond the clock-skew allowance) cannot be a real past reading, so it must not eclipse
                // valid events nor become the selected "newest" (review: future-dated observations).
                if (evt.Timestamp - fetchedAt > MaxFutureSkew)
                {
                    continue;
                }

                // Strict '>' — a later candidate never displaces an equal-or-newer earlier one; ties
                // (same millisecond across files) keep the first seen. Real files never tie this finely.
                if (newest is null || evt.Timestamp > newest.Timestamp)
                {
                    newest = evt;
                }
            }
        }

        return newest is null
            ? BuildUnavailable("no-recent-event", fetchedAt)
            : MapEvent(newest, fetchedAt);
    }

    /// <summary>
    /// Parse a single tail into its usable <c>token_count</c> events, in the order they appear.
    /// Unparseable lines (fragments, partial writes, non-JSON) and non-usable events (wrong type,
    /// no timestamp, null/malformed <c>primary</c>) are silently skipped.
    /// </summary>
    public static IEnumerable<CodexUsageEvent> ParseUsableEvents(string tail)
    {
        if (string.IsNullOrEmpty(tail))
        {
            yield break;
        }

        // Split on '\n'; JSON tolerates the surrounding whitespace (incl. a trailing '\r'), so no trim
        // is needed. The 64 KB bound on tails keeps this allocation small.
        foreach (var line in tail.Split('\n'))
        {
            if (TryParseTokenCountLine(line, out var evt))
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Try to parse ONE JSONL line into a usable <see cref="CodexUsageEvent"/>. Returns <c>false</c>
    /// (never throws) for any line that is not a well-formed, timestamped <c>token_count</c> event with
    /// a usable non-null <c>primary</c> window — that total tolerance is the load-bearing property for
    /// live/partially-written session files.
    /// </summary>
    public static bool TryParseTokenCountLine(string line, [NotNullWhen(true)] out CodexUsageEvent? evt)
    {
        evt = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, EventMsgType, StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) ||
                !string.Equals(payloadType, TokenCountPayloadType, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetTimestamp(root, out var timestamp))
            {
                return false;
            }

            if (!payload.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // A usable non-null primary window is mandatory (e3-findings §e3 "6-step" step 3): this is
            // exactly what skips the degenerate `primary:null` trailing event and falls through to a
            // real reading in an older line/file.
            var primary = ReadWindow(rateLimits, "primary", timestamp);
            if (primary is null)
            {
                return false;
            }

            var secondary = ReadWindow(rateLimits, "secondary", timestamp);

            string? creditsBalanceRaw = null;
            if (rateLimits.TryGetProperty("credits", out var credits) &&
                credits.ValueKind == JsonValueKind.Object &&
                credits.TryGetProperty("balance", out var balance) &&
                balance.ValueKind == JsonValueKind.String)
            {
                creditsBalanceRaw = balance.GetString();
            }

            string? planType = null;
            if (rateLimits.TryGetProperty("plan_type", out var plan) && plan.ValueKind == JsonValueKind.String)
            {
                planType = plan.GetString();
            }

            evt = new CodexUsageEvent(timestamp, primary, secondary, creditsBalanceRaw, planType);
            return true;
        }
        catch (JsonException)
        {
            // Fragment / partial / malformed line — the common, expected case for a 64 KB tail's first
            // line and a live file's last line. Skip it.
            return false;
        }
        catch (Exception)
        {
            // Any other structural surprise degrades to "skip this line", never to a provider outage.
            return false;
        }
    }

    /// <summary>
    /// Build a whole-source <see cref="SourceStatus.Unavailable"/> snapshot carrying
    /// <paramref name="reasonCode"/> on the status and on every metric (e.g. <c>"no-sessions-dir"</c>,
    /// <c>"no-recent-event"</c>). The provider card still renders — a visible n/a is part of the contract.
    /// </summary>
    public static ProviderSnapshot BuildUnavailable(string reasonCode, DateTimeOffset fetchedAt) => new(
        ProviderId: CodexProvider.ProviderId,
        FetchedAt: fetchedAt,
        Status: SourceStatus.Unavailable,
        StatusReasonCode: reasonCode,
        Windows: Array.Empty<UsageWindow>(),
        CreditsBalance: Metric.Unavailable<decimal>(reasonCode),
        PlanType: Metric.Unavailable<string>(reasonCode));

    private static ProviderSnapshot MapEvent(CodexUsageEvent evt, DateTimeOffset fetchedAt)
    {
        var observedAt = evt.Timestamp;

        var windows = new List<UsageWindow> { ToWindow(evt.Primary, observedAt) };
        if (evt.Secondary is { } secondary)
        {
            windows.Add(ToWindow(secondary, observedAt));
        }

        // Deterministic display order by the window's authoritative minute-count (5h before weekly),
        // stable across the era flip that swapped which window sits in `primary`. Order is display-only —
        // window identity remains WindowMinutes, never list position (DESIGN.md §3).
        windows.Sort(static (a, b) => a.WindowMinutes.CompareTo(b.WindowMinutes));

        return new ProviderSnapshot(
            ProviderId: CodexProvider.ProviderId,
            FetchedAt: fetchedAt,
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: windows,
            CreditsBalance: MapCredits(evt.CreditsBalanceRaw, observedAt),
            PlanType: MapPlan(evt.PlanType, observedAt));
    }

    private static UsageWindow ToWindow(CodexWindowReading window, DateTimeOffset observedAt)
    {
        var usedPercent = Metric.Available(window.UsedPercent, observedAt);
        var resetsAt = window.ResetsAt is { } reset
            ? Metric.Available(reset, observedAt)
            : Metric.NotApplicable<DateTimeOffset>("not-reported");

        return new UsageWindow(
            WindowMinutes: window.WindowMinutes,
            Label: WindowClassifier.Label(window.WindowMinutes),
            UsedPercent: usedPercent,
            ResetsAt: resetsAt);
    }

    private static Metric<decimal> MapCredits(string? raw, DateTimeOffset observedAt)
    {
        // Absent / null credits object or balance → the concept does not apply here (older schema, or a
        // plan that does not report a balance): NotApplicable, never a fabricated zero.
        if (raw is null || raw.Length == 0)
        {
            return Metric.NotApplicable<decimal>("not-reported");
        }

        // "0E-10" and friends: exponent form is legal decimal; parse invariantly, preserving precision.
        return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var balance)
            ? Metric.Available(balance, observedAt)
            : Metric.Unavailable<decimal>("source-changed"); // present but unparseable = drift, not "n/a doesn't apply"
    }

    private static Metric<string> MapPlan(string? planType, DateTimeOffset observedAt)
        => planType is { Length: > 0 } value
            ? Metric.Available(value, observedAt)
            : Metric.NotApplicable<string>("not-reported");

    /// <summary>
    /// Read a <c>primary</c>/<c>secondary</c> window object into a <see cref="CodexWindowReading"/>, or
    /// return <c>null</c> when the property is absent, JSON <c>null</c>, or malformed (missing/typed-wrong
    /// <c>used_percent</c> or <c>window_minutes</c>). A malformed window is treated as absent — for
    /// <c>primary</c> that skips the whole event; for <c>secondary</c> it just drops that window.
    /// </summary>
    private static CodexWindowReading? ReadWindow(JsonElement rateLimits, string name, DateTimeOffset eventTimestamp)
    {
        if (!rateLimits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetDecimal(window, "used_percent", out var usedPercent) ||
            !TryGetInt32(window, "window_minutes", out var windowMinutes))
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        try
        {
            if (TryGetInt64(window, "resets_at", out var epochSeconds))
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            }
            else if (TryGetInt64(window, "resets_in_seconds", out var relativeSeconds))
            {
                resetsAt = eventTimestamp.AddSeconds(relativeSeconds);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A nonsensical epoch/offset can't yield a coherent reset; the window still carries a usable
            // used_percent, so keep the window and mark only its reset NotApplicable downstream.
            resetsAt = null;
        }

        return new CodexWindowReading(windowMinutes, usedPercent, resetsAt);
    }

    private static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        return false;
    }

    private static bool TryGetDecimal(JsonElement element, string name, out decimal value)
    {
        value = 0m;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out value);
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0L;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!root.TryGetProperty("timestamp", out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // The embedded timestamp is UTC ISO-8601 with 'Z' (e3-findings §2). Normalise to UTC so all
        // selection/age comparisons happen on one timeline.
        if (property.TryGetDateTimeOffset(out var parsed))
        {
            timestamp = parsed.ToUniversalTime();
            return true;
        }

        var text = property.GetString();
        if (text is not null && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
        {
            timestamp = parsed;
            return true;
        }

        return false;
    }
}
