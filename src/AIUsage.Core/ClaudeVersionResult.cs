namespace AIUsage.Core;

/// <summary>
/// Where a resolved Claude Code version came from, in the E2 preference order
/// (<c>e2-findings.md</c> §4). Recorded so a caller can tell a fresh LIVE read from a fall-back on the
/// last-known-good cache — the distinction the accuracy contract cares about.
/// </summary>
public enum ClaudeVersionSource
{
    /// <summary>The canonical spawn-free source: <c>@anthropic-ai/claude-code/package.json</c> <c>.version</c> (E2 §2a).</summary>
    PackageJson,

    /// <summary>Spawn-free fallback: the <c>claude.exe</c> Windows <c>ProductVersion</c>, trailing <c>.0</c> trimmed (E2 §2b).</summary>
    FileVersion,

    /// <summary>Fragile live fallback: the leading semver parsed from a time-limited <c>claude --version</c> (E2 §2/§3).</summary>
    Cli,

    /// <summary>No live source resolved this run — served from the persisted last-known-good cache (E2 §4).</summary>
    Cache,
}

/// <summary>
/// A resolved Claude Code version and the <see cref="ClaudeVersionSource"/> it came from
/// (<c>e2-findings.md</c>). Used to build the <c>User-Agent: claude-code/&lt;version&gt;</c> the probe
/// sends on the undocumented usage endpoint. A <c>null</c> result (never this record with a blank
/// version) means no version has EVER been resolved on this machine → the provider shows
/// <c>n/a("no-version")</c>. A version is NEVER fabricated — a cached previously-real value is truthful,
/// a made-up one is not (DESIGN.md §4.2).
/// </summary>
/// <param name="Version">The 3-part semver (e.g. <c>"2.1.191"</c>) — validated <c>^\d+\.\d+\.\d+$</c>, never blank.</param>
/// <param name="Source">Which source produced it this run.</param>
public sealed record ClaudeVersionResult(string Version, ClaudeVersionSource Source);
