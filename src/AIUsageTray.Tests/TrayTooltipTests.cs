using AIUsage.Core;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers the tooltip render logic (task T12): LIVE renders an explicit <c>% used</c>, non-live renders
/// a literal <c>n/a</c> (never a blank that could imply zero), and the string is always capped so the
/// render path can never throw on length.
/// </summary>
public sealed class TrayTooltipTests
{
    private static UsageView BuildCodexView(UsageWindow window, DateTimeOffset now)
    {
        var snapshot = new ProviderSnapshot(
            CodexProvider.ProviderId,
            now,
            SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: new[] { window },
            CreditsBalance: Metric.Unavailable<decimal>("not-reported"),
            PlanType: Metric.Unavailable<string>("not-reported"));

        var snapshots = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal)
        {
            [snapshot.ProviderId] = snapshot,
        };

        var lastKnown = new LastKnownReadingStore();
        lastKnown.RecordFrom(snapshot);

        return UsageViewBuilder.Build(snapshots, lastKnown, DisplayConfig.Default, TimeProvider.System);
    }

    [Fact]
    public void LiveWindow_RendersExplicitPercentUsed()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(
            WindowClassifier.WeeklyMinutes,
            "Weekly",
            Metric.Available(4m, now),
            Metric.Available(now.AddDays(3), now));

        var text = TrayTooltip.Build(BuildCodexView(window, now), TrayTooltip.MaxLength);

        Assert.Contains("Codex: Weekly 4% used", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableWindow_RendersExplicitNaNeverBlank()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(
            WindowClassifier.WeeklyMinutes,
            "Weekly",
            Metric.Unavailable<decimal>("not-reported"),
            Metric.Available(now.AddDays(3), now));

        var text = TrayTooltip.Build(BuildCodexView(window, now), TrayTooltip.MaxLength);

        Assert.Contains("Codex: Weekly n/a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("% used", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledClaude_RendersOffNotImpliedZero()
    {
        var now = DateTimeOffset.UtcNow;
        // A disabled Claude publishes an Unavailable("disabled") snapshot with no windows.
        var snapshot = new ProviderSnapshot(
            ClaudeProvider.ProviderId,
            now,
            SourceStatus.Unavailable,
            "disabled",
            Array.Empty<UsageWindow>(),
            Metric.Unavailable<decimal>("disabled"),
            Metric.Unavailable<string>("disabled"));
        var snapshots = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal)
        {
            [snapshot.ProviderId] = snapshot,
        };
        var view = UsageViewBuilder.Build(snapshots, new LastKnownReadingStore(), DisplayConfig.Default, TimeProvider.System);

        var text = TrayTooltip.Build(view, TrayTooltip.MaxLength);

        Assert.Contains("Claude: off", text, StringComparison.Ordinal);
        Assert.DoesNotContain("% used", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NeverExceedsRequestedLength()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new UsageWindow(
            WindowClassifier.WeeklyMinutes,
            "Weekly",
            Metric.Available(73.5m, now),
            Metric.Available(now.AddDays(3), now));

        var text = TrayTooltip.Build(BuildCodexView(window, now), maxLength: 8);

        Assert.True(text.Length <= 8, $"Tooltip length {text.Length} exceeded the cap.");
    }

    [Fact]
    public void TwoLongProviderLines_NeitherIsDroppedEntirely()
    {
        // A long Claude line must not truncate the Codex line away at the 127-char cap (review P2-17).
        var now = DateTimeOffset.UtcNow;

        static WindowView Live(string providerId, string label, decimal percent, DateTimeOffset now) => new(
            ProviderId: providerId,
            WindowMinutes: WindowClassifier.WeeklyMinutes,
            Label: label,
            DisplayState: DisplayState.Live,
            Percent: percent,
            ObservedAt: now,
            ResetsAt: Metric.Available(now.AddDays(3), now),
            Severity: Severity.Normal,
            ReasonCode: null);

        var claude = new ProviderView(
            "claude", now, SourceStatus.Ok, null,
            new[] { Live("claude", "5h", 34.7m, now), Live("claude", "Weekly", 61.2m, now), Live("claude", "Fable wk", 83.9m, now) },
            Metric.Available(8.75m, now), Metric.Available("Max", now), Severity.Warning, false, false);

        var codex = new ProviderView(
            "codex", now, SourceStatus.Ok, null,
            new[] { Live("codex", "5h", 4.0m, now), Live("codex", "Weekly", 82.4m, now), Live("codex", "24h", 51.9m, now) },
            Metric.Available(12.40m, now), Metric.Available("Pro", now), Severity.Warning, false, false);

        var view = new UsageView(Severity.Warning, Unknown: false, AllUnknown: false, new[] { claude, codex });

        var text = TrayTooltip.Build(view, TrayTooltip.MaxLength);

        Assert.True(text.Length <= TrayTooltip.MaxLength, $"Tooltip length {text.Length} exceeded the cap.");
        Assert.Contains("Claude:", text, StringComparison.Ordinal);
        Assert.Contains("Codex:", text, StringComparison.Ordinal);
    }
}
