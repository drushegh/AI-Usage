using System.Globalization;
using System.Text.Json;

namespace AIUsage.Core.Tests;

/// <summary>
/// Test-data helpers for the Codex collector: reads the DEFINITIVE E3 fixtures (linked into the test
/// output under <c>Fixtures/</c>) and synthesises well-formed <c>token_count</c> JSONL lines with
/// controlled timestamps/values. Synthetic lines are built via <see cref="JsonSerializer"/> so they are
/// always valid JSON with invariant number formatting — the parser, not the test's string plumbing, is
/// what is under test.
/// </summary>
internal static class CodexTestData
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Read a linked E3 fixture file verbatim (e.g. <c>"fixture-weekly-only.jsonl"</c>).</summary>
    public static string ReadFixture(string name) => File.ReadAllText(Path.Combine(FixtureDirectory, name));

    /// <summary>
    /// Build one <c>token_count</c> JSONL line. Defaults produce a current-schema weekly-only event; the
    /// parameters expose the axes the tests vary (timestamp, used%, window, reset encoding, null primary,
    /// plan, credits, and the two type discriminators).
    /// </summary>
    public static string MakeLine(
        DateTimeOffset timestamp,
        decimal? primaryUsed = 5m,
        int primaryWindow = 10080,
        long? primaryResetsAt = 1785016789L,
        long? primaryResetsInSeconds = null,
        bool primaryNull = false,
        string? planType = "plus",
        string? creditsBalance = null,
        string payloadType = "token_count",
        string type = "event_msg")
    {
        object? primary = primaryNull
            ? null
            : BuildWindow(primaryUsed, primaryWindow, primaryResetsAt, primaryResetsInSeconds);

        object? credits = creditsBalance is null
            ? null
            : new Dictionary<string, object?> { ["has_credits"] = false, ["balance"] = creditsBalance };

        var rateLimits = new Dictionary<string, object?>
        {
            ["primary"] = primary,
            ["secondary"] = null,
            ["credits"] = credits,
            ["plan_type"] = planType,
        };

        var payload = new Dictionary<string, object?>
        {
            ["type"] = payloadType,
            ["rate_limits"] = rateLimits,
        };

        var root = new Dictionary<string, object?>
        {
            ["timestamp"] = timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["type"] = type,
            ["payload"] = payload,
        };

        return JsonSerializer.Serialize(root);
    }

    private static Dictionary<string, object?> BuildWindow(decimal? used, int window, long? resetsAt, long? resetsInSeconds)
    {
        var w = new Dictionary<string, object?> { ["window_minutes"] = window };
        if (used is { } u)
        {
            w["used_percent"] = u;
        }

        if (resetsAt is { } ra)
        {
            w["resets_at"] = ra;
        }
        else if (resetsInSeconds is { } ri)
        {
            w["resets_in_seconds"] = ri;
        }

        return w;
    }
}

/// <summary>A scratch directory that deletes itself on <see cref="Dispose"/> — for provider file-I/O tests.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "codex-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
