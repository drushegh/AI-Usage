namespace AIUsage.Core;

/// <summary>
/// The concrete paths the <see cref="ClaudeVersionResolver"/> resolves against (<c>e2-findings.md</c>
/// §1–§4). Passed in rather than hard-coded so tests point the resolver at temp directories and fakes;
/// production builds them from <see cref="Environment.SpecialFolder"/> anchors via
/// <see cref="ForCurrentMachine"/> — never a literal <c>C:\Users\…</c> string, so a minimal (PATH-less)
/// logon environment still resolves them (E2 §3: <c>%APPDATA%</c>/<c>%LOCALAPPDATA%</c> are set for the
/// logon session even when PATH is stripped).
/// </summary>
/// <param name="PackageJsonPath">
/// <c>@anthropic-ai/claude-code/package.json</c> — the canonical spawn-free <c>.version</c> source (E2 §2a).
/// </param>
/// <param name="ExePaths">
/// <c>claude.exe</c> candidates whose Windows <c>ProductVersion</c> is the spawn-free fallback (E2 §2b),
/// most-likely first: the npm-global binary, then the native-installer probe locations.
/// </param>
/// <param name="ClaudeCommand">
/// The executable to spawn for the time-limited <c>claude --version</c> fallback (E2 §2/§3) — the absolute
/// npm-global exe path in production. <c>null</c> skips the spawn step entirely.
/// </param>
/// <param name="CachePath">
/// The persisted last-known-good cache (E2 §4): <c>%LOCALAPPDATA%\AIUsage\claude-version.json</c>. In
/// <c>%LOCALAPPDATA%</c> (not roaming <c>%APPDATA%</c>) so a machine-specific installed version never
/// roams to a different PC.
/// </param>
public sealed record ClaudeVersionPaths(
    string PackageJsonPath,
    IReadOnlyList<string> ExePaths,
    string? ClaudeCommand,
    string CachePath)
{
    /// <summary>The tray's own per-user data dir under <c>%LOCALAPPDATA%</c> (never <c>~/.claude</c>, which is Claude Code's — E2 §2/§4).</summary>
    public const string AppDataFolderName = "AIUsage";

    /// <summary>The cache file name under <see cref="AppDataFolderName"/> (E2 §4).</summary>
    public const string CacheFileName = "claude-version.json";

    /// <summary>
    /// Build the production paths from environment anchors (E2 §1–§4): the npm-global package dir under
    /// <c>%APPDATA%\npm\node_modules\@anthropic-ai\claude-code</c>, the native-installer probe locations
    /// under <c>%LOCALAPPDATA%</c> / <c>%USERPROFILE%</c>, and the cache under <c>%LOCALAPPDATA%\AIUsage</c>.
    /// Paths are returned whether or not the targets exist — a missing source is simply a resolver MISS.
    /// </summary>
    public static ClaudeVersionPaths ForCurrentMachine()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);         // %APPDATA% (Roaming)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); // %LOCALAPPDATA%
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);         // %USERPROFILE%

        var npmPackageDir = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code");
        var npmExe = Path.Combine(npmPackageDir, "bin", "claude.exe");

        var exePaths = new[]
        {
            npmExe,                                                                  // npm-global (E2 §1/§2b)
            Path.Combine(localAppData, "Programs", "claude-code", "claude.exe"),     // native-installer probe (E2 §3)
            Path.Combine(userProfile, ".local", "bin", "claude.exe"),               // native-installer probe (E2 §3)
        };

        return new ClaudeVersionPaths(
            PackageJsonPath: Path.Combine(npmPackageDir, "package.json"),
            ExePaths: exePaths,
            ClaudeCommand: npmExe, // spawn the absolute exe, not bare "claude" (PATH may be stripped at logon — E2 §3)
            CachePath: Path.Combine(localAppData, AppDataFolderName, CacheFileName));
    }
}
