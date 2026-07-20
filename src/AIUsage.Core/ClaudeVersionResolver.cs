using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsage.Core;

/// <summary>
/// Resolves the installed Claude Code version for the <c>User-Agent: claude-code/&lt;version&gt;</c> the
/// probe sends on the undocumented usage endpoint (a wrong/absent UA → 429). It implements the E2
/// resolution ORDER (<c>e2-findings.md</c> §4) and is the only place that logic lives — the filesystem,
/// process-spawn, and clock are SEAMS (<see cref="IVersionFileSystem"/>,
/// <see cref="IClaudeVersionCommandRunner"/>, <see cref="TimeProvider"/>), so the order is unit-tested
/// deterministically against temp files and fakes, never a real spawn or a real machine path.
/// </summary>
/// <remarks>
/// Order (fastest / most reliable first — E2 §4):
/// <list type="number">
///   <item><b>package.json</b> <c>.version</c> — canonical, spawn-free, exact <c>^\d+\.\d+\.\d+$</c>.</item>
///   <item><b>exe <c>ProductVersion</c></b> — spawn-free; the leading semver (trailing <c>.0</c> trimmed),
///   tried across the candidate exe paths in order.</item>
///   <item><b>time-limited <c>claude --version</c></b> — the fragile fallback; leading semver of stdout.</item>
///   <item><b>last-known-good cache</b> — only when 1–3 all miss (e.g. a PATH-less logon before disk is warm).</item>
///   <item><b><c>null</c></b> — only when the cache has NEVER held a value. A version is NEVER fabricated.</item>
/// </list>
/// Every successful LIVE resolve (1–3) writes through to the cache, so the last-known-good is always the
/// newest value actually observed. The <c>.last-update-result.json</c> update log is deliberately NOT a
/// source (E2 trap — it can name a version that never became live).
/// </remarks>
public sealed class ClaudeVersionResolver : IClaudeVersionSource
{
    /// <summary>Default hard cap for the <c>claude --version</c> spawn (E2 §3: warm ≈0.5 s, cold logon slower — budget 3–5 s).</summary>
    public static readonly TimeSpan DefaultCliTimeout = TimeSpan.FromSeconds(5);

    // A full-string 3-part semver (package.json is required to be exactly this — E2 §4).
    private static readonly Regex ExactSemver =
        new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The LEADING 3-part semver of a longer string: "2.1.191.0" → "2.1.191" (trims the 4th FileVersion
    // component), "2.1.191 (Claude Code)" → "2.1.191" (the CLI banner). E2 §2b/§2.
    private static readonly Regex LeadingSemver =
        new(@"^\s*(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ClaudeVersionPaths _paths;
    private readonly IVersionFileSystem _fileSystem;
    private readonly IClaudeVersionCommandRunner _commandRunner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cliTimeout;

    /// <param name="paths">The sources + cache location (production: <see cref="ClaudeVersionPaths.ForCurrentMachine"/>; tests: temp paths).</param>
    /// <param name="fileSystem">Filesystem seam for the spawn-free sources and the cache.</param>
    /// <param name="commandRunner">Process seam for the time-limited <c>claude --version</c> fallback.</param>
    /// <param name="timeProvider">Clock seam for the cache's <c>resolvedAt</c> stamp — never <see cref="DateTimeOffset.UtcNow"/> directly.</param>
    /// <param name="cliTimeout">Override for <see cref="DefaultCliTimeout"/> (tests pass a small value; the fake runner ignores it anyway).</param>
    public ClaudeVersionResolver(
        ClaudeVersionPaths paths,
        IVersionFileSystem fileSystem,
        IClaudeVersionCommandRunner commandRunner,
        TimeProvider timeProvider,
        TimeSpan? cliTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _paths = paths;
        _fileSystem = fileSystem;
        _commandRunner = commandRunner;
        _timeProvider = timeProvider;
        _cliTimeout = cliTimeout ?? DefaultCliTimeout;
    }

    /// <summary>
    /// Resolve the version per the E2 order. Returns the resolved <see cref="ClaudeVersionResult"/>, or
    /// <c>null</c> when nothing has EVER resolved on this machine (the provider then shows
    /// <c>n/a("no-version")</c>). A live resolve (1–3) writes through to the cache before returning.
    /// </summary>
    public ClaudeVersionResult? Resolve()
    {
        // 1a. package.json .version — canonical, spawn-free.
        if (TryExtractPackageJsonVersion(_fileSystem.ReadText(_paths.PackageJsonPath), out var packageVersion))
        {
            return LiveResolve(packageVersion, ClaudeVersionSource.PackageJson);
        }

        // 1b. exe ProductVersion — spawn-free, install-shape-agnostic; first candidate that yields a semver wins.
        foreach (var exePath in _paths.ExePaths)
        {
            if (TryNormalizeLeadingSemver(_fileSystem.ReadExeProductVersion(exePath), out var exeVersion))
            {
                return LiveResolve(exeVersion, ClaudeVersionSource.FileVersion);
            }
        }

        // 2. time-limited `claude --version` — the fragile live fallback.
        if (!string.IsNullOrWhiteSpace(_paths.ClaudeCommand) &&
            TryNormalizeLeadingSemver(_commandRunner.Run(_paths.ClaudeCommand, _cliTimeout), out var cliVersion))
        {
            return LiveResolve(cliVersion, ClaudeVersionSource.Cli);
        }

        // 3. last-known-good cache — only when 1–3 all missed.
        var cached = ReadCachedVersion();
        if (cached is not null)
        {
            return new ClaudeVersionResult(cached, ClaudeVersionSource.Cache);
        }

        // 4. never resolved on this machine → n/a("no-version"). Never fabricate.
        return null;
    }

    private ClaudeVersionResult LiveResolve(string version, ClaudeVersionSource source)
    {
        WriteCache(version, source);
        return new ClaudeVersionResult(version, source);
    }

    private void WriteCache(string version, ClaudeVersionSource source)
    {
        var entry = new CacheEntry(version, SourceToken(source), _timeProvider.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
        _fileSystem.WriteText(_paths.CachePath, JsonSerializer.Serialize(entry, CacheJsonOptions));
    }

    private string? ReadCachedVersion()
    {
        var text = _fileSystem.ReadText(_paths.CachePath);
        if (text is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String)
            {
                var value = version.GetString();
                // A malformed cached value is a MISS, not a value — never trust a non-semver from disk.
                if (value is not null && ExactSemver.IsMatch(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt cache file is a miss, never an error.
        }

        return null;
    }

    private static bool TryExtractPackageJsonVersion(string? packageJsonText, out string version)
    {
        version = string.Empty;
        if (packageJsonText is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(packageJsonText);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("version", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                // package.json must be EXACTLY x.y.z (E2 §4) — no trimming, a malformed read is a miss.
                if (text is not null && ExactSemver.IsMatch(text))
                {
                    version = text;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON / not an object → miss.
        }

        return false;
    }

    private static bool TryNormalizeLeadingSemver(string? raw, out string version)
    {
        version = string.Empty;
        if (raw is null)
        {
            return false;
        }

        var match = LeadingSemver.Match(raw);
        if (match.Success)
        {
            version = match.Groups[1].Value;
            return true;
        }

        return false;
    }

    private static string SourceToken(ClaudeVersionSource source) => source switch
    {
        ClaudeVersionSource.PackageJson => "package.json",
        ClaudeVersionSource.FileVersion => "fileversion",
        ClaudeVersionSource.Cli => "cli",
        // Cache is a read-through source and is never written back through here.
        _ => source.ToString(),
    };

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The persisted cache shape (E2 §4). Serialized camelCase → <c>version</c>/<c>source</c>/<c>resolvedAt</c>.</summary>
    private sealed record CacheEntry(string Version, string Source, string ResolvedAt);
}
