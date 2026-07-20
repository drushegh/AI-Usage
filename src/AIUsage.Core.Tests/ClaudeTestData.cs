using System.Text.Json;

namespace AIUsage.Core.Tests;

/// <summary>
/// Test-data helpers for the Claude parser: reads the DEFINITIVE E1 capture (linked into the test output
/// under <c>Fixtures/</c>) and synthesises well-formed <c>oauth/usage</c> bodies with controlled parts
/// for the malformed / edge axes. Synthetic bodies are built via <see cref="JsonSerializer"/> so they are
/// always valid JSON with invariant number formatting — the parser, not the test's string plumbing, is
/// what is under test.
/// </summary>
internal static class ClaudeTestData
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>The canonical E1 capture (HTTP 200, verified token/PII-free) used as the primary fixture.</summary>
    public const string FixtureName = "raw-capture-1.json";

    /// <summary>A valid ISO-8601-with-offset reset instant for synthetic bodies.</summary>
    public const string SampleResetsAt = "2026-07-20T19:59:59.592703+00:00";

    public static string ReadFixture() => File.ReadAllText(Path.Combine(FixtureDirectory, FixtureName));

    /// <summary>Build a <c>five_hour</c>/<c>seven_day</c>-style object with a float utilization + reset.</summary>
    public static Dictionary<string, object?> UtilObject(object? utilization, string? resetsAt = SampleResetsAt)
        => new()
        {
            ["utilization"] = utilization,
            ["resets_at"] = resetsAt,
            ["limit_dollars"] = null,
            ["used_dollars"] = null,
            ["remaining_dollars"] = null,
        };

    /// <summary>Build one <c>limits[]</c> entry. <paramref name="percent"/> may be an int, a string, etc. to drive the typed-wrong / range axes.</summary>
    public static Dictionary<string, object?> Limit(
        string kind,
        object? percent,
        string? resetsAt = SampleResetsAt,
        bool isActive = false,
        string? scopeModel = null,
        string severity = "normal")
    {
        object? scope = scopeModel is null
            ? null
            : new Dictionary<string, object?>
            {
                ["model"] = new Dictionary<string, object?> { ["id"] = null, ["display_name"] = scopeModel },
                ["surface"] = null,
            };

        return new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["group"] = kind == "session" ? "session" : "weekly",
            ["percent"] = percent,
            ["severity"] = severity,
            ["resets_at"] = resetsAt,
            ["scope"] = scope,
            ["is_active"] = isActive,
        };
    }

    /// <summary>A disabled <c>spend</c> block (matches the E1 fixture: <c>enabled:false</c>, zero minor units).</summary>
    public static Dictionary<string, object?> DisabledSpend() => new()
    {
        ["used"] = new Dictionary<string, object?> { ["amount_minor"] = 0, ["currency"] = "USD", ["exponent"] = 2 },
        ["enabled"] = false,
    };

    /// <summary>An enabled <c>spend</c> block with the given minor units + exponent (E1-pinned dollars math).</summary>
    public static Dictionary<string, object?> EnabledSpend(long amountMinor, int exponent) => new()
    {
        ["used"] = new Dictionary<string, object?> { ["amount_minor"] = amountMinor, ["currency"] = "USD", ["exponent"] = exponent },
        ["enabled"] = true,
    };

    /// <summary>
    /// Serialize a full body. Only the parts passed are included; the <paramref name="nullBuckets"/> flag
    /// sprinkles the real E1 null codename buckets in to prove tolerance.
    /// </summary>
    public static string Body(
        IEnumerable<Dictionary<string, object?>>? limits = null,
        Dictionary<string, object?>? fiveHour = null,
        Dictionary<string, object?>? sevenDay = null,
        Dictionary<string, object?>? spend = null,
        Dictionary<string, object?>? extraUsage = null,
        bool nullBuckets = false)
    {
        var root = new Dictionary<string, object?>();

        if (fiveHour is not null)
        {
            root["five_hour"] = fiveHour;
        }

        if (sevenDay is not null)
        {
            root["seven_day"] = sevenDay;
        }

        if (nullBuckets)
        {
            root["seven_day_opus"] = null;
            root["seven_day_sonnet"] = null;
            root["tangelo"] = null;
            root["iguana_necktie"] = null;
        }

        if (extraUsage is not null)
        {
            root["extra_usage"] = extraUsage;
        }

        if (limits is not null)
        {
            root["limits"] = limits.ToList();
        }

        if (spend is not null)
        {
            root["spend"] = spend;
        }

        return JsonSerializer.Serialize(root);
    }
}
