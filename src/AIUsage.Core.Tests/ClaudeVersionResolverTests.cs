using System.Text.Json;
using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Tests for the E2 resolution ORDER (<c>e2-findings.md</c> §4): package.json → exe FileVersion →
/// time-limited <c>claude --version</c> → last-known-good cache → null. Deterministic via an in-memory
/// filesystem + a fake command runner (no real spawns, no real machine paths); one test exercises the
/// production <see cref="SystemVersionFileSystem"/> against a real TEMP dir for the round-trip.
/// </summary>
public sealed class ClaudeVersionResolverTests
{
    private const string PackageJsonPath = "PKG";
    private const string ExeA = "EXE_A";
    private const string ExeB = "EXE_B";
    private const string Command = "CMD";
    private const string CachePath = "CACHE";

    private static readonly DateTimeOffset Now = new(2026, 07, 20, 12, 00, 00, TimeSpan.Zero);

    private static ClaudeVersionPaths FakePaths(string? command = Command) =>
        new(PackageJsonPath, new[] { ExeA, ExeB }, command, CachePath);

    private static ClaudeVersionResolver Resolver(
        IVersionFileSystem fs, IClaudeVersionCommandRunner runner, ClaudeVersionPaths? paths = null) =>
        new(paths ?? FakePaths(), fs, runner, new FakeTimeProvider(Now));

    private static string PackageJson(string version) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["name"] = "@anthropic-ai/claude-code", ["version"] = version });

    // ---- 1. package.json present -> its .version -----------------------------------------------

    [Fact]
    public void PackageJsonPresent_ReturnsItsVersion_AndDoesNotSpawn()
    {
        var fs = new FakeVersionFileSystem().SetText(PackageJsonPath, PackageJson("2.1.191"));
        var runner = new FakeClaudeVersionCommandRunner("9.9.9");

        var result = Resolver(fs, runner).Resolve();

        Assert.NotNull(result);
        Assert.Equal("2.1.191", result!.Version);
        Assert.Equal(ClaudeVersionSource.PackageJson, result.Source);
        Assert.Equal(0, runner.Calls); // spawn-free path short-circuits before the CLI fallback
    }

    // ---- 2. package.json absent -> exe FileVersion (trailing .0 trimmed) -----------------------

    [Fact]
    public void PackageJsonAbsent_FallsBackToExeProductVersion_TrimsTrailingZero()
    {
        var fs = new FakeVersionFileSystem().SetExeVersion(ExeA, "2.1.191.0");
        var runner = new FakeClaudeVersionCommandRunner("9.9.9");

        var result = Resolver(fs, runner).Resolve();

        Assert.Equal("2.1.191", result!.Version); // 4-part FileVersion → 3-part semver
        Assert.Equal(ClaudeVersionSource.FileVersion, result.Source);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void ExeFileVersion_TriesCandidatesInOrder_FirstUsableWins()
    {
        // First candidate has no metadata; the second yields a version.
        var fs = new FakeVersionFileSystem().SetExeVersion(ExeB, "3.0.5.0");

        var result = Resolver(fs, new FakeClaudeVersionCommandRunner()).Resolve();

        Assert.Equal("3.0.5", result!.Version);
        Assert.Equal(ClaudeVersionSource.FileVersion, result.Source);
    }

    // ---- 3. package.json + exe absent -> `claude --version` ------------------------------------

    [Fact]
    public void LiveMetadataAbsent_ParsesCliOutput()
    {
        var fs = new FakeVersionFileSystem();
        var runner = new FakeClaudeVersionCommandRunner("2.1.191 (Claude Code)\n");

        var result = Resolver(fs, runner).Resolve();

        Assert.Equal("2.1.191", result!.Version); // leading semver of the banner
        Assert.Equal(ClaudeVersionSource.Cli, result.Source);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(Command, runner.LastCommand);
    }

    [Fact]
    public void NoClaudeCommand_SkipsCliStep()
    {
        var fs = new FakeVersionFileSystem();
        var runner = new FakeClaudeVersionCommandRunner("2.1.191");

        // ClaudeCommand null → the spawn step is skipped entirely.
        var result = Resolver(fs, runner, FakePaths(command: null)).Resolve();

        Assert.Null(result);
        Assert.Equal(0, runner.Calls);
    }

    // ---- 4. all live sources absent -> last-known-good cache ------------------------------------

    [Fact]
    public void AllLiveSourcesAbsent_FallsBackToCache()
    {
        var fs = new FakeVersionFileSystem().SetText(CachePath, PackageJson("2.0.9")); // {"version":"2.0.9"}
        var runner = new FakeClaudeVersionCommandRunner(); // returns null

        var result = Resolver(fs, runner).Resolve();

        Assert.Equal("2.0.9", result!.Version);
        Assert.Equal(ClaudeVersionSource.Cache, result.Source);
    }

    [Fact]
    public void CacheWithMalformedVersion_IsMiss()
    {
        var fs = new FakeVersionFileSystem().SetText(CachePath, PackageJson("not-a-semver"));

        var result = Resolver(fs, new FakeClaudeVersionCommandRunner()).Resolve();

        Assert.Null(result); // a non-semver cached value is not trusted
    }

    // ---- 5. nothing ever -> null (never fabricate) ---------------------------------------------

    [Fact]
    public void NothingEverResolved_ReturnsNull()
    {
        var result = Resolver(new FakeVersionFileSystem(), new FakeClaudeVersionCommandRunner()).Resolve();

        Assert.Null(result);
    }

    // ---- 6. a successful live resolve writes the cache (round-trip) -----------------------------

    [Fact]
    public void SuccessfulLiveResolve_WritesThroughCache_ReadableBack()
    {
        var fs = new FakeVersionFileSystem().SetText(PackageJsonPath, PackageJson("2.1.191"));

        var first = Resolver(fs, new FakeClaudeVersionCommandRunner()).Resolve();

        Assert.Equal(ClaudeVersionSource.PackageJson, first!.Source);
        Assert.Contains(CachePath, fs.Writes); // wrote through
        var cacheText = fs.ReadText(CachePath);
        Assert.NotNull(cacheText);
        Assert.Contains("2.1.191", cacheText);

        // A second resolver with ONLY the cache present reads the persisted value back.
        var cacheOnly = new FakeVersionFileSystem().SetText(CachePath, cacheText!);
        var second = Resolver(cacheOnly, new FakeClaudeVersionCommandRunner()).Resolve();

        Assert.Equal("2.1.191", second!.Version);
        Assert.Equal(ClaudeVersionSource.Cache, second.Source);
    }

    // ---- Priority: package.json wins over every lower source -----------------------------------

    [Fact]
    public void ResolutionOrder_IsStrict_PackageJsonBeatsExeAndCli()
    {
        var fs = new FakeVersionFileSystem()
            .SetText(PackageJsonPath, PackageJson("1.0.0"))
            .SetExeVersion(ExeA, "2.0.0.0");
        var runner = new FakeClaudeVersionCommandRunner("3.0.0");

        var result = Resolver(fs, runner).Resolve();

        Assert.Equal("1.0.0", result!.Version);
        Assert.Equal(ClaudeVersionSource.PackageJson, result.Source);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void MalformedPackageJson_IsMiss_FallsThroughToExe()
    {
        var fs = new FakeVersionFileSystem()
            .SetText(PackageJsonPath, "{ this is not json")
            .SetExeVersion(ExeA, "4.5.6.0");

        var result = Resolver(fs, new FakeClaudeVersionCommandRunner()).Resolve();

        Assert.Equal("4.5.6", result!.Version);
        Assert.Equal(ClaudeVersionSource.FileVersion, result.Source);
    }

    // ---- Production adapter over a real TEMP dir (no real machine paths) ------------------------

    [Fact]
    public void SystemFileSystem_OverRealTempDir_ResolvesPackageJson_AndCacheRoundTrips()
    {
        using var temp = new TempDir();
        var pkgPath = Path.Combine(temp.Root, "package.json");
        var cachePath = Path.Combine(temp.Root, "sub", "claude-version.json"); // sub dir must be created on write
        File.WriteAllText(pkgPath, PackageJson("2.1.191"));

        var paths = new ClaudeVersionPaths(
            PackageJsonPath: pkgPath,
            ExePaths: new[] { Path.Combine(temp.Root, "nope.exe") },
            ClaudeCommand: null,
            CachePath: cachePath);

        var fs = new SystemVersionFileSystem();

        var first = new ClaudeVersionResolver(paths, fs, new FakeClaudeVersionCommandRunner(), new FakeTimeProvider(Now)).Resolve();
        Assert.Equal("2.1.191", first!.Version);
        Assert.Equal(ClaudeVersionSource.PackageJson, first.Source);
        Assert.True(File.Exists(cachePath)); // write-through created the file (and its parent dir)

        // Now remove the live source: the resolver must fall back to the on-disk cache it just wrote.
        File.Delete(pkgPath);
        var second = new ClaudeVersionResolver(paths, fs, new FakeClaudeVersionCommandRunner(), new FakeTimeProvider(Now)).Resolve();
        Assert.Equal("2.1.191", second!.Version);
        Assert.Equal(ClaudeVersionSource.Cache, second.Source);
    }
}
