using System.IO;
using AIUsage.Core;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers the persisted config (T6/T41): round-trips of the Claude flag AND the tunable thresholds/TTL,
/// the robustness contract (missing/corrupt → defaults, never throws), the legacy-file default fill-in for
/// the newer knobs, and the on-load clamp that keeps a hand-edited bad file out of the display engine.
/// </summary>
public sealed class AppConfigTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"aiusage-cfg-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_MissingFile_ReturnsDefaultClaudeOn()
    {
        var store = new AppConfigStore(_path);

        var config = store.Load();

        Assert.True(config.ClaudeEnabled); // default ON — owner's own account
        Assert.Equal(AppConfig.Default, config);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsClaudeEnabled()
    {
        var store = new AppConfigStore(_path);

        store.Save(new AppConfig(ClaudeEnabled: true));

        Assert.True(store.Load().ClaudeEnabled);
        // The persisted shape is the documented { "claudeEnabled": true } contract.
        Assert.Contains("\"claudeEnabled\": true", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_FirstRunNotYetShown()
    {
        var store = new AppConfigStore(_path);

        // A fresh install has not shown the overflow-pin hint yet (T40) — so it will show once.
        Assert.False(store.Load().FirstRunShown);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFirstRunShown()
    {
        var store = new AppConfigStore(_path);

        store.Save(new AppConfig(ClaudeEnabled: true, FirstRunShown: true));

        Assert.True(store.Load().FirstRunShown);
        // The persisted shape carries the documented { "firstRunShown": true } contract.
        Assert.Contains("\"firstRunShown\": true", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_LegacyFileWithoutFirstRunField_DefaultsToNotShown()
    {
        // A config written before T40 (only claudeEnabled) must deserialise FirstRunShown to false, so
        // existing installs also get the one-time hint.
        File.WriteAllText(_path, "{ \"claudeEnabled\": true }");
        var store = new AppConfigStore(_path);

        var config = store.Load();

        Assert.True(config.ClaudeEnabled);
        Assert.False(config.FirstRunShown);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultWithoutThrowing()
    {
        File.WriteAllText(_path, "{ this is not valid json ");
        var store = new AppConfigStore(_path);

        var config = store.Load(); // must not throw

        Assert.True(config.ClaudeEnabled); // corrupt → default (ON)
    }

    [Fact]
    public void Default_MatchesDisplayConfigDefault()
    {
        // The shipped AppConfig defaults must project to the shipped DisplayConfig defaults (80 / 90 / 20m),
        // so a fresh install and a saved-defaults file both reproduce the shipped display behaviour.
        Assert.Equal(DisplayConfig.Default, AppConfig.Default.ToDisplayConfig());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThresholdsAndTtl()
    {
        var store = new AppConfigStore(_path);

        store.Save(new AppConfig(ClaudeEnabled: true, FirstRunShown: false,
            WarnPercent: 85m, CritPercent: 95m, CodexTtlMinutes: 30));

        var loaded = store.Load();
        Assert.Equal(85m, loaded.WarnPercent);
        Assert.Equal(95m, loaded.CritPercent);
        Assert.Equal(30, loaded.CodexTtlMinutes);
        // The persisted shape carries the documented camelCase knobs.
        Assert.Contains("\"warnPercent\": 85", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_LegacyFileWithoutNewFields_FillsThresholdDefaults()
    {
        // A config written before the Settings window (only claudeEnabled) must deserialise the newer knobs
        // to their DEFAULTS (80 / 90 / 20), never to a value-type zero.
        File.WriteAllText(_path, "{ \"claudeEnabled\": true, \"firstRunShown\": true }");
        var store = new AppConfigStore(_path);

        var config = store.Load();

        Assert.Equal(80m, config.WarnPercent);
        Assert.Equal(90m, config.CritPercent);
        Assert.Equal(20, config.CodexTtlMinutes);
    }

    [Fact]
    public void Load_ClampsOutOfRangeValues()
    {
        // A hand-edited file with wild values must be clamped so the display engine never sees them:
        // warn 0 → 1, crit 500 → 100, ttl 99999 → 1440.
        File.WriteAllText(_path,
            "{ \"claudeEnabled\": true, \"warnPercent\": 0, \"critPercent\": 500, \"codexTtlMinutes\": 99999 }");
        var store = new AppConfigStore(_path);

        var config = store.Load();

        Assert.Equal(1m, config.WarnPercent);
        Assert.Equal(100m, config.CritPercent);
        Assert.Equal(1440, config.CodexTtlMinutes);
    }

    [Fact]
    public void Load_ClampsCriticalAboveWarningWhenFileInverts()
    {
        // crit <= warn in the file → crit is lifted to strictly above the (clamped) warn.
        File.WriteAllText(_path,
            "{ \"claudeEnabled\": true, \"warnPercent\": 95, \"critPercent\": 90, \"codexTtlMinutes\": 20 }");
        var store = new AppConfigStore(_path);

        var config = store.Load();

        Assert.Equal(95m, config.WarnPercent);
        Assert.Equal(96m, config.CritPercent); // warn + 1
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
