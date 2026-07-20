using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace AIUsage.Core;

/// <summary>
/// The pure, I/O-FREE core of the Claude collector (DESIGN.md §4.2 "Schema validation", §5; spike
/// <c>e1-findings.md</c>). It turns the raw <c>oauth/usage</c> response BODY (untrusted probe output —
/// DESIGN.md §2b) into a <see cref="ClaudeUsageParseResult"/>, with no filesystem, clock, or network of
/// its own: the observation time comes in as <paramref name="observedAt"/> (the fetch time — the body
/// carries no observation timestamp, only future <c>resets_at</c> instants). Keeping the highest-risk
/// logic — parsing a real, undocumented, codename-laden payload — a pure function is what makes it
/// exhaustively testable against the E1 fixture.
/// </summary>
/// <remarks>
/// PINNED semantics, hard-coded from the E1 capture (never inferred at runtime):
/// <list type="bullet">
///   <item><b>Utilization scale is 0–100</b> (float, e.g. <c>five_hour.utilization = 28.0</c>) — NEVER a
///   0–1 fraction. <c>limits[].percent</c> is the same number as an integer (the int rounding).</item>
///   <item><b>Render primarily from <c>limits[]</c></b>: each <c>{ kind, group, percent, severity,
///   resets_at, scope, is_active }</c>. <c>session</c>→5h (300m), <c>weekly_all</c>→weekly (10080m),
///   <c>weekly_scoped</c>→a per-model weekly labelled from <c>scope.model.display_name</c>. The float
///   <c>five_hour</c>/<c>seven_day</c> utilizations are PREFERRED for precision on session/weekly_all.</item>
///   <item><b><c>resets_at</c></b> is ISO-8601 with a tz offset → <see cref="DateTimeOffset"/>.</item>
///   <item><b>Tolerate unknown/extra fields.</b> The body carries a crowd of null codename buckets
///   (<c>seven_day_opus</c>, <c>tangelo</c>, <c>iguana_necktie</c>, …); their presence/absence is NEVER
///   drift. A strict "unknown field = drift" parser would fail on first contact (E1).</item>
/// </list>
/// Degradation ladder (DESIGN.md §5): a missing/typed-wrong REQUIRED field degrades only its OWN metric
/// (that window's used-% or reset → n/a) and never a sibling; a body that is non-JSON, not an object, or
/// carries NEITHER a usable <c>limits[]</c> entry NOR a usable <c>five_hour</c> → an overall
/// <see cref="ClaudeUsageParseResult.IsDrift"/> result (<c>"source-changed"</c>) that the provider maps
/// to all-Claude n/a. Response bodies are NEVER returned or logged — only a names-and-kinds
/// <see cref="ClaudeUsageParseResult.SchemaSignature"/>.
/// </remarks>
public static class ClaudeUsageParser
{
    /// <summary>The always-<c>"source-changed"</c> reason a drift result carries (DESIGN.md §5).</summary>
    private const string DriftReasonCode = "source-changed";

    /// <summary>The authoritative minute-count of the session (~5h) window (E1: <c>kind:"session"</c>).</summary>
    private const int SessionWindowMinutes = WindowClassifier.FiveHourMinutes;

    /// <summary>The authoritative minute-count of every weekly window (E1: <c>kind:"weekly_all"</c>/<c>"weekly_scoped"</c>).</summary>
    private const int WeeklyWindowMinutes = WindowClassifier.WeeklyMinutes;

    /// <summary>
    /// Parse and validate one <c>oauth/usage</c> response body into the current Claude reading. See the
    /// type remarks for the pinned semantics and the drift ladder.
    /// </summary>
    /// <param name="body">The raw response body (untrusted probe stdout). Null/blank/non-JSON → a drift result.</param>
    /// <param name="observedAt">
    /// When the response was received (the provider's fetch time). Becomes every Available metric's
    /// <see cref="Metric{T}.ObservedAt"/> — the body itself has no observation timestamp.
    /// </param>
    public static ClaudeUsageParseResult Parse(string? body, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            // Empty / whitespace is not a payload — the envelope is untrustable, no signature to record.
            return Drift(signature: null);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Non-JSON (an HTML error page, a truncated body, …) → drift; never surface the body.
            return Drift(signature: null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Drift(signature: null);
            }

            var signature = BuildSchemaSignature(root);

            // Float precision anchors (E1): prefer these over the int limits[].percent for session/weekly_all.
            // A present-but-out-of-range or typed-wrong utilization yields a null anchor → fall back to the int.
            var fiveHourUtil = ReadValidUtilization(root, "five_hour");
            var sevenDayUtil = ReadValidUtilization(root, "seven_day");

            // PRIMARY source: the limits[] array (E1 §3 — the richer, canonical shape).
            var fromLimits = BuildFromLimits(root, fiveHourUtil, sevenDayUtil, observedAt);
            if (fromLimits.Count > 0 && AnyUsable(fromLimits))
            {
                return Usable(fromLimits, root, observedAt, signature);
            }

            // FALLBACK: synthesize session/weekly windows from the top-level five_hour/seven_day objects
            // when limits[] is absent, empty, or produced nothing usable.
            var fromTopLevel = BuildFromTopLevel(root, observedAt);
            if (AnyUsable(fromTopLevel))
            {
                return Usable(fromTopLevel, root, observedAt, signature);
            }

            // Neither limits[] nor five_hour yielded a usable window → the envelope is untrustable.
            return Drift(signature);
        }
    }

    private static bool AnyUsable(IReadOnlyList<ClaudeUsageWindow> windows)
    {
        // "Usable" = a numeric utilization in 0..100 AND a parseable resets_at, both Available (the gate
        // stated in the task / DESIGN.md §5). A window with only one of the two is still rendered, but it
        // does not, on its own, make the whole envelope trustworthy.
        foreach (var w in windows)
        {
            if (w.Window.UsedPercent.State == MetricState.Available &&
                w.Window.ResetsAt.State == MetricState.Available)
            {
                return true;
            }
        }

        return false;
    }

    private static List<ClaudeUsageWindow> BuildFromLimits(
        JsonElement root, decimal? fiveHourUtil, decimal? sevenDayUtil, DateTimeOffset observedAt)
    {
        var windows = new List<ClaudeUsageWindow>();
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return windows;
        }

        // Ordinal of nameless weekly_scoped entries seen so far, so two of them never collide on one key
        // (review NEW-4). Only nameless scoped windows consume it — a named scoped window keys off its name.
        var namelessScopedCount = 0;

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !TryGetString(entry, "kind", out var kind))
            {
                continue; // a non-object entry or one without a kind is unknown → tolerate, skip
            }

            int windowMinutes;
            string label;
            decimal? anchor;
            string? scopeModel = null;
            string? scopeId = null;

            switch (kind)
            {
                case "session":
                    windowMinutes = SessionWindowMinutes;
                    label = WindowClassifier.Label(SessionWindowMinutes); // "5h"
                    anchor = fiveHourUtil;
                    break;

                case "weekly_all":
                    windowMinutes = WeeklyWindowMinutes;
                    label = WindowClassifier.Label(WeeklyWindowMinutes); // "Weekly"
                    anchor = sevenDayUtil;
                    break;

                case "weekly_scoped":
                    windowMinutes = WeeklyWindowMinutes;
                    scopeModel = ReadScopeModelName(entry);
                    if (scopeModel is { Length: > 0 } name)
                    {
                        // Per-model window ("Fable wk"), keyed distinctly from weekly_all so their history,
                        // severity, and toast arming never merge (review P0-1).
                        label = $"{name} wk";
                        scopeId = name;
                    }
                    else
                    {
                        // A scoped window with NO model name must NOT masquerade as the all-models "Weekly"
                        // (review P0-1 / Sol) — give it a distinct label AND a distinct key. Two nameless
                        // scoped entries must also not collide P0-1-style (review NEW-4): the first keeps the
                        // bare "scoped" key (backward-compatible), each subsequent one is disambiguated by its
                        // ordinal ("scoped#1", "scoped#2", …) so their history / severity / arming stay separate.
                        label = "Scoped wk";
                        scopeId = namelessScopedCount == 0
                            ? "scoped"
                            : $"scoped#{namelessScopedCount.ToString(CultureInfo.InvariantCulture)}";
                        namelessScopedCount++;
                    }

                    // No top-level float anchor exists for a scoped window, so its int percent is authoritative.
                    anchor = null;
                    break;

                default:
                    continue; // an unknown kind is additive tolerance, never drift (E1)
            }

            var usedPercent = BuildPercent(entry, "percent", anchor, observedAt);
            var resetsAt = BuildReset(entry, "resets_at", observedAt);
            var isActive = TryGetBool(entry, "is_active") ?? false;
            TryGetString(entry, "severity", out var severity);

            windows.Add(new ClaudeUsageWindow(
                new UsageWindow(windowMinutes, label, usedPercent, resetsAt, scopeId),
                IsActive: isActive,
                Severity: severity,
                ScopeModel: scopeModel));
        }

        return windows;
    }

    private static List<ClaudeUsageWindow> BuildFromTopLevel(JsonElement root, DateTimeOffset observedAt)
    {
        var windows = new List<ClaudeUsageWindow>();

        if (root.TryGetProperty("five_hour", out var fiveHour) && fiveHour.ValueKind == JsonValueKind.Object)
        {
            windows.Add(new ClaudeUsageWindow(
                new UsageWindow(
                    SessionWindowMinutes,
                    WindowClassifier.Label(SessionWindowMinutes),
                    BuildPercent(fiveHour, "utilization", anchor: null, observedAt),
                    BuildReset(fiveHour, "resets_at", observedAt)),
                IsActive: false,
                Severity: null,
                ScopeModel: null));
        }

        if (root.TryGetProperty("seven_day", out var sevenDay) && sevenDay.ValueKind == JsonValueKind.Object)
        {
            windows.Add(new ClaudeUsageWindow(
                new UsageWindow(
                    WeeklyWindowMinutes,
                    WindowClassifier.Label(WeeklyWindowMinutes),
                    BuildPercent(sevenDay, "utilization", anchor: null, observedAt),
                    BuildReset(sevenDay, "resets_at", observedAt)),
                IsActive: false,
                Severity: null,
                ScopeModel: null));
        }

        return windows;
    }

    /// <summary>
    /// Build the used-% metric. When a valid top-level float <paramref name="anchor"/> exists it is
    /// PREFERRED (E1 precision rule) and the int is ignored. Otherwise the container's numeric field is
    /// read and range-checked: absent/null → n/a "not-reported"; wrong type → "source-changed";
    /// out of 0..100 → "out-of-range" (NEVER clamped — DESIGN.md §5).
    /// </summary>
    private static Metric<decimal> BuildPercent(JsonElement container, string prop, decimal? anchor, DateTimeOffset observedAt)
    {
        if (anchor is decimal preferred)
        {
            return Metric.Available(preferred, observedAt);
        }

        if (!container.TryGetProperty(prop, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Metric.NotApplicable<decimal>("not-reported");
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var percent))
        {
            return Metric.Unavailable<decimal>("source-changed");
        }

        if (percent < 0m || percent > 100m)
        {
            return Metric.Unavailable<decimal>("out-of-range");
        }

        return Metric.Available(percent, observedAt);
    }

    /// <summary>
    /// Build the reset-instant metric from an ISO-8601-with-offset string (E1). Absent/null →
    /// "not-reported"; a non-string or an unparseable string → "source-changed".
    /// </summary>
    private static Metric<DateTimeOffset> BuildReset(JsonElement container, string prop, DateTimeOffset observedAt)
    {
        if (!container.TryGetProperty(prop, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Metric.NotApplicable<DateTimeOffset>("not-reported");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return Metric.Unavailable<DateTimeOffset>("source-changed");
        }

        // Preserve the source offset (E1: "+00:00" here) — the display layer localises it later.
        if (value.TryGetDateTimeOffset(out var parsed))
        {
            return Metric.Available(parsed, observedAt);
        }

        var text = value.GetString();
        if (text is not null && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
        {
            return Metric.Available(parsed, observedAt);
        }

        return Metric.Unavailable<DateTimeOffset>("source-changed");
    }

    /// <summary>
    /// The credit / extra-usage balance (DESIGN.md §7 — shown "only when the response supplies value AND
    /// meaningful unit"). The Claude <c>oauth/usage</c> body (E1) exposes <c>spend</c> (amount SPENT) and/or
    /// <c>extra_usage</c>, but carries NO genuine credit BALANCE with a unit. "Amount spent" is not a balance
    /// (review P0-5 / Sol), and <c>spend.used</c> is unitless minor-units of consumption — so it is NEVER
    /// surfaced as a balance. Until the payload carries a real balance, Claude credits are always n/a: an
    /// enabled <c>extra_usage</c> whose value units E1 has NOT pinned → n/a "scale-unpinned" (never a
    /// fabricated number — the HARD RULE); an enabled <c>spend</c> (spend, not balance) → n/a "no-balance";
    /// a switched-off block → "disabled"; neither block present → "not-reported".
    /// </summary>
    private static Metric<decimal> MapCredits(JsonElement root)
    {
        var hasSpend = root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object;
        var hasExtra = root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object;

        // Enabled extra usage means credits apply, but E1 never pinned its value units → refuse to guess.
        if (hasExtra && TryGetBool(extra, "is_enabled") == true)
        {
            return Metric.NotApplicable<decimal>("scale-unpinned");
        }

        // Enabled spend carries `spend.used` (amount spent), which is NOT a balance and has no balance unit.
        if (hasSpend && TryGetBool(spend, "enabled") == true)
        {
            return Metric.NotApplicable<decimal>("no-balance");
        }

        if (hasSpend || hasExtra)
        {
            return Metric.NotApplicable<decimal>("disabled"); // block present but switched off
        }

        return Metric.NotApplicable<decimal>("not-reported");
    }

    /// <summary>
    /// The plan / tier metric. The <c>oauth/usage</c> body carries no plan field today (E1), so this is
    /// normally n/a "not-reported"; a future <c>plan_type</c>/<c>plan</c> string is tolerated additively.
    /// </summary>
    private static Metric<string> MapPlan(JsonElement root, DateTimeOffset observedAt)
    {
        if ((TryGetString(root, "plan_type", out var plan) || TryGetString(root, "plan", out plan)) &&
            plan.Length > 0)
        {
            return Metric.Available(plan, observedAt);
        }

        return Metric.NotApplicable<string>("not-reported");
    }

    private static string? ReadScopeModelName(JsonElement entry)
    {
        if (entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
            scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
            TryGetString(model, "display_name", out var name))
        {
            return name;
        }

        return null;
    }

    /// <summary>
    /// A top-level <c>utilization</c> float that is a number in 0..100, else <c>null</c>. Used as the
    /// preferred-precision anchor for the session (<c>five_hour</c>) and weekly_all (<c>seven_day</c>)
    /// windows; a present-but-invalid value returns null so the caller falls back to the int percent.
    /// </summary>
    private static decimal? ReadValidUtilization(JsonElement root, string objectName)
    {
        if (root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty("utilization", out var util) && util.ValueKind == JsonValueKind.Number &&
            util.TryGetDecimal(out var value) && value >= 0m && value <= 100m)
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// A non-sensitive fingerprint of the top-level object: each field's NAME and JSON KIND (never a
    /// value), sorted, comma-joined. Codenames like <c>tangelo</c> are product internals, not user data
    /// (E1 deems the name/type signature explicitly non-sensitive). This is the input to the provider's
    /// "record a signature when new names/types appear" rule (DESIGN.md §4.2).
    /// </summary>
    private static string BuildSchemaSignature(JsonElement root)
    {
        var parts = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            parts.Add($"{property.Name}:{property.Value.ValueKind}");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static ClaudeUsageParseResult Usable(
        IReadOnlyList<ClaudeUsageWindow> windows, JsonElement root, DateTimeOffset observedAt, string? signature)
        => new(
            Windows: windows,
            Credits: MapCredits(root),
            PlanType: MapPlan(root, observedAt),
            IsDrift: false,
            DriftReason: null,
            SchemaSignature: signature);

    private static ClaudeUsageParseResult Drift(string? signature)
        => new(
            Windows: Array.Empty<ClaudeUsageWindow>(),
            Credits: Metric.Unavailable<decimal>(DriftReasonCode),
            PlanType: Metric.Unavailable<string>(DriftReasonCode),
            IsDrift: true,
            DriftReason: DriftReasonCode,
            SchemaSignature: signature);

    // ---- Small JSON read helpers (kept private/local; mirror CodexSessionParser's own set — the two
    //      parsers are self-contained so a change to one can never silently perturb the other). --------

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

    private static bool? TryGetBool(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

}
