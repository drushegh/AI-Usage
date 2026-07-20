using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers the pure settings validation/clamp helper (T41/T38): the on-load clamp that keeps a bad file out
/// of the display engine, and the Settings-window input validation (1 ≤ warn &lt; crit ≤ 100, 1..1440 min).
/// </summary>
public sealed class SettingsValidationTests
{
    private static AppConfig Config(decimal warn, decimal crit, int ttl)
        => new(ClaudeEnabled: true, FirstRunShown: false, WarnPercent: warn, CritPercent: crit, CodexTtlMinutes: ttl);

    [Fact]
    public void Clamp_InRangeValues_Unchanged()
    {
        var result = SettingsValidation.Clamp(Config(80m, 90m, 20));

        Assert.Equal(80m, result.WarnPercent);
        Assert.Equal(90m, result.CritPercent);
        Assert.Equal(20, result.CodexTtlMinutes);
    }

    [Theory]
    [InlineData(0, 1)]      // below the 1 floor
    [InlineData(-50, 1)]
    [InlineData(150, 99)]   // above the 99 ceiling (must leave room for a greater crit)
    [InlineData(99, 99)]
    public void Clamp_Warning_ClampedToOneThroughNinetyNine(double warn, double expected)
    {
        var result = SettingsValidation.Clamp(Config((decimal)warn, 100m, 20));
        Assert.Equal((decimal)expected, result.WarnPercent);
    }

    [Fact]
    public void Clamp_CriticalNotAboveWarning_LiftedToWarningPlusOne()
    {
        var result = SettingsValidation.Clamp(Config(80m, 70m, 20));
        Assert.Equal(81m, result.CritPercent);
    }

    [Fact]
    public void Clamp_CriticalAboveHundred_ClampedToHundred()
    {
        var result = SettingsValidation.Clamp(Config(80m, 500m, 20));
        Assert.Equal(100m, result.CritPercent);
    }

    [Fact]
    public void Clamp_WarningNinetyNine_ForcesCriticalToHundred()
    {
        // The tightest corner: warn clamps to 99, so crit's lower bound is 100 == the ceiling.
        var result = SettingsValidation.Clamp(Config(99m, 50m, 20));
        Assert.Equal(99m, result.WarnPercent);
        Assert.Equal(100m, result.CritPercent);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99999, 1440)]
    [InlineData(20, 20)]
    public void Clamp_Ttl_ClampedToOneThrough1440(int ttl, int expected)
    {
        var result = SettingsValidation.Clamp(Config(80m, 90m, ttl));
        Assert.Equal(expected, result.CodexTtlMinutes);
    }

    [Fact]
    public void Validate_ValidInput_ReturnsNull()
    {
        Assert.Null(SettingsValidation.Validate(80m, 90m, 20));
    }

    [Theory]
    [InlineData(0, 90, 20)]    // warn below 1
    [InlineData(100, 90, 20)]  // warn above 99
    public void Validate_BadWarning_ReturnsMessage(double warn, double crit, int ttl)
    {
        Assert.NotNull(SettingsValidation.Validate((decimal)warn, (decimal)crit, ttl));
    }

    [Theory]
    [InlineData(80, 80, 20)]   // crit == warn
    [InlineData(80, 70, 20)]   // crit < warn
    [InlineData(80, 101, 20)]  // crit > 100
    public void Validate_BadCritical_ReturnsMessage(double warn, double crit, int ttl)
    {
        Assert.NotNull(SettingsValidation.Validate((decimal)warn, (decimal)crit, ttl));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Validate_BadTtl_ReturnsMessage(int ttl)
    {
        Assert.NotNull(SettingsValidation.Validate(80m, 90m, ttl));
    }
}
