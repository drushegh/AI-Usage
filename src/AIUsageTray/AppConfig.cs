using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// The tiny persisted tray config (<c>%LOCALAPPDATA%\AIUsage\config.json</c>) — the tunable slice of
/// T6/T41. It carries the Claude opt-in flag (<see cref="ClaudeEnabled"/>), the one-time first-run hint
/// marker (<see cref="FirstRunShown"/>), and the owner-tunable display knobs surfaced by the Settings
/// window (<see cref="WarnPercent"/>, <see cref="CritPercent"/>, <see cref="CodexTtlMinutes"/>). It is
/// loaded once at startup — to seed <see cref="ClaudeProvider.Enabled"/> and the initial
/// <see cref="DisplayConfig"/> (see <see cref="ToDisplayConfig"/>) — and saved whenever the menu toggle
/// or the Settings window changes anything.
/// </summary>
/// <remarks>
/// The threshold/TTL defaults intentionally MATCH <see cref="DisplayConfig.Default"/> (80 / 90 / 20 min),
/// so a fresh install and a saved-defaults file both round-trip to the shipped display behaviour. The
/// invariant is asserted in tests. Claude defaults ON — this is a single-owner personal tool reading its
/// OWNER's own account (the undocumented/ToS-grey caveat, DESIGN.md §4.2, is a one-line note in the
/// Settings window and README, not a default-off gate); the menu and Settings window still let the owner
/// turn it off.
/// </remarks>
/// <param name="ClaudeEnabled">Whether the Claude usage provider is opted in. Defaults to <c>true</c>.</param>
/// <param name="FirstRunShown">
/// Whether the first-run "pin me out of the overflow flyout" hint (DESIGN.md §7 Windows integration 1;
/// task T40) has already been shown. Defaults to <c>false</c> so a fresh install shows it once, then
/// flips to <c>true</c> and persists.
/// </param>
/// <param name="WarnPercent">Warning severity threshold (default 80). Compared against the UNROUNDED percent.</param>
/// <param name="CritPercent">Critical severity threshold (default 90). Compared against the UNROUNDED percent.</param>
/// <param name="CodexTtlMinutes">
/// How long a Codex reading counts as LIVE, in minutes (default 20; DESIGN.md §5 LIVE rule 3). "Longer
/// than your longest typical turn, short enough that unseen usage stays bounded."
/// </param>
public sealed record AppConfig(
    bool ClaudeEnabled,
    bool FirstRunShown = false,
    decimal WarnPercent = 80m,
    decimal CritPercent = 90m,
    int CodexTtlMinutes = 20,
    // ---- USER-SET display fallbacks (owner feedback r4). Shown ONLY when the provider has not
    // reported the value, always attributed as the owner's own setting, never styled LIVE. These do
    // NOT feed DisplayConfig / the accuracy engine — they are the owner's ground truth for a gap, not
    // an app-invented figure (the HARD RULE forbids the app estimating provider data, not the owner
    // filling in what they already know). Null = not set. ----
    string? ClaudePlan = null,
    string? CodexPlan = null,
    DayOfWeek? CodexWeeklyResetDay = null,
    string? CodexWeeklyResetTime = null)
{
    /// <summary>The shipped default: Claude ON, first-run hint not yet shown, thresholds/TTL = <see cref="DisplayConfig.Default"/>.</summary>
    public static AppConfig Default { get; } = new(ClaudeEnabled: true, FirstRunShown: false);

    /// <summary>
    /// Project the persisted knobs into the in-memory <see cref="DisplayConfig"/> the icon renderer,
    /// notification decider, and <see cref="UsageViewBuilder"/> read. The Claude "current" TTL is not
    /// owner-tunable (it is pinned to the 180s poll cadence + margin, DESIGN.md §5), so it always comes
    /// from <see cref="DisplayConfig.Default"/>.
    /// </summary>
    public DisplayConfig ToDisplayConfig() => new(
        CodexCurrentTtl: TimeSpan.FromMinutes(CodexTtlMinutes),
        ClaudeCurrentTtl: DisplayConfig.Default.ClaudeCurrentTtl,
        WarnPercent: WarnPercent,
        CritPercent: CritPercent);

    /// <summary>
    /// The next occurrence (in <paramref name="nowLocal"/>'s zone) of the owner's configured Codex weekly
    /// reset (day-of-week + <c>HH:mm</c>), or <c>null</c> if unset or the time is unparseable. USER-PROVIDED
    /// fallback only — surfaced when the provider hasn't reported a reset, always tagged "your setting",
    /// never a LIVE countdown styled as provider-reported (accuracy contract).
    /// </summary>
    public DateTimeOffset? NextCodexWeeklyReset(DateTimeOffset nowLocal)
    {
        if (CodexWeeklyResetDay is not { } day) return null;
        if (!TimeOnly.TryParse(CodexWeeklyResetTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;

        int delta = ((int)day - (int)nowLocal.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(
            nowLocal.Year, nowLocal.Month, nowLocal.Day, time.Hour, time.Minute, 0, nowLocal.Offset).AddDays(delta);
        if (candidate <= nowLocal) candidate = candidate.AddDays(7);
        return candidate;
    }
}

/// <summary>
/// The owner's manually-entered values used only to fill gaps the providers haven't reported (owner
/// feedback r4). Built from <see cref="AppConfig"/> and handed to the popup, which renders each ONLY when
/// the matching provider metric is n/a, always attributed as the owner's own setting. Never LIVE-styled.
/// </summary>
/// <param name="ClaudePlan">Owner's Claude plan label, or null.</param>
/// <param name="CodexPlan">Owner's Codex plan label, or null.</param>
/// <param name="CodexWeeklyReset">Next occurrence of the owner's Codex weekly-reset schedule, or null.</param>
public sealed record UserFallbacks(string? ClaudePlan, string? CodexPlan, DateTimeOffset? CodexWeeklyReset)
{
    /// <summary>Empty — nothing configured.</summary>
    public static UserFallbacks None { get; } = new(null, null, null);

    /// <summary>The owner's plan fallback for a provider id, or null if none is set for it.</summary>
    public string? PlanFor(string providerId)
        => string.Equals(providerId, "claude", StringComparison.OrdinalIgnoreCase) ? ClaudePlan
         : string.Equals(providerId, "codex", StringComparison.OrdinalIgnoreCase) ? CodexPlan
         : null;
}

/// <summary>
/// Load/save seam for <see cref="AppConfig"/>. Deliberately ROBUST on the UI thread: a missing or corrupt
/// file resolves to <see cref="AppConfig.Default"/> and <see cref="Load"/> NEVER throws; <see cref="Save"/>
/// is best-effort and swallows every I/O fault (persisting settings must never take the tray down). On
/// load the tunable knobs are CLAMPED (<see cref="SettingsValidation.Clamp"/>) so a hand-edited bad file
/// can't feed an out-of-range threshold into the display engine.
/// </summary>
public sealed class AppConfigStore
{
    private readonly string _path;

    /// <param name="path">
    /// Config file path. Defaults to <see cref="DefaultPath"/> (<c>%LOCALAPPDATA%\AIUsage\config.json</c>);
    /// tests pass a temp path.
    /// </param>
    public AppConfigStore(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>The config file path this store reads and writes.</summary>
    public string Path => _path;

    /// <summary>
    /// The default config location: <c>%LOCALAPPDATA%\AIUsage\config.json</c> — the same per-user
    /// <see cref="ClaudeVersionPaths.AppDataFolderName"/> folder the version cache lives in, in
    /// LOCALAPPDATA (not roaming) so a machine-specific opt-in never roams to another PC.
    /// </summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ClaudeVersionPaths.AppDataFolderName,
        "config.json");

    /// <summary>
    /// Read the persisted config, or <see cref="AppConfig.Default"/> when the file is absent, unreadable,
    /// or not valid JSON. A legacy file missing the newer knobs deserialises them to their defaults (not to
    /// zero); every tunable is then clamped into range. Never throws — a corrupt file is treated as
    /// "no config yet", not an error.
    /// </summary>
    public AppConfig Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AppConfig.Default;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ConfigDto>(text, JsonOptions);
            if (dto is null)
            {
                return AppConfig.Default;
            }

            // Absent (null) knob → the shipped default; present → the stored value. This is why the DTO
            // fields are nullable: a pre-Settings file (only claudeEnabled) must land on 80/90/20, NOT the
            // 0/0/0 a non-nullable value type would deserialise to.
            var config = new AppConfig(
                dto.ClaudeEnabled,
                dto.FirstRunShown,
                dto.WarnPercent ?? AppConfig.Default.WarnPercent,
                dto.CritPercent ?? AppConfig.Default.CritPercent,
                dto.CodexTtlMinutes ?? AppConfig.Default.CodexTtlMinutes,
                string.IsNullOrWhiteSpace(dto.ClaudePlan) ? null : dto.ClaudePlan.Trim(),
                string.IsNullOrWhiteSpace(dto.CodexPlan) ? null : dto.CodexPlan.Trim(),
                dto.CodexWeeklyResetDay,
                string.IsNullOrWhiteSpace(dto.CodexWeeklyResetTime) ? null : dto.CodexWeeklyResetTime.Trim());

            return SettingsValidation.Clamp(config);
        }
        catch (JsonException)
        {
            // Corrupt/partial file → defaults, never a throw on the UI startup path.
            return AppConfig.Default;
        }
    }

    /// <summary>
    /// Persist <paramref name="config"/>, creating the parent directory if needed. Best-effort: any I/O or
    /// serialisation fault is swallowed so a failed write can never surface on the UI settings path.
    /// </summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new ConfigDto
                {
                    ClaudeEnabled = config.ClaudeEnabled,
                    FirstRunShown = config.FirstRunShown,
                    WarnPercent = config.WarnPercent,
                    CritPercent = config.CritPercent,
                    CodexTtlMinutes = config.CodexTtlMinutes,
                    ClaudePlan = config.ClaudePlan,
                    CodexPlan = config.CodexPlan,
                    CodexWeeklyResetDay = config.CodexWeeklyResetDay,
                    CodexWeeklyResetTime = config.CodexWeeklyResetTime,
                },
                JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best-effort persistence — a save failure is not fatal (the in-memory change already applied).
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// The persisted shape. Serialises camelCase → <c>{ "claudeEnabled", "firstRunShown", "warnPercent",
    /// "critPercent", "codexTtlMinutes" }</c>. The tunable knobs are NULLABLE so an older file that predates
    /// them deserialises to <c>null</c> (→ default) rather than a value-type zero.
    /// </summary>
    private sealed class ConfigDto
    {
        [JsonPropertyName("claudeEnabled")]
        public bool ClaudeEnabled { get; set; }

        [JsonPropertyName("firstRunShown")]
        public bool FirstRunShown { get; set; }

        [JsonPropertyName("warnPercent")]
        public decimal? WarnPercent { get; set; }

        [JsonPropertyName("critPercent")]
        public decimal? CritPercent { get; set; }

        [JsonPropertyName("codexTtlMinutes")]
        public int? CodexTtlMinutes { get; set; }

        [JsonPropertyName("claudePlan")]
        public string? ClaudePlan { get; set; }

        [JsonPropertyName("codexPlan")]
        public string? CodexPlan { get; set; }

        [JsonPropertyName("codexWeeklyResetDay")]
        public DayOfWeek? CodexWeeklyResetDay { get; set; }

        [JsonPropertyName("codexWeeklyResetTime")]
        public string? CodexWeeklyResetTime { get; set; }
    }
}
