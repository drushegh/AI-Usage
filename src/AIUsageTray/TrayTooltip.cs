using System.Text;
using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// Builds the compact tray tooltip from a <see cref="UsageView"/> (DESIGN.md §7 Tooltip; task T12):
/// one line per provider, each window labelled with its explicit <c>% used</c> figure — or a literal
/// <c>n/a</c> with a short reason. Pure and side-effect-free so it is unit-testable without WinForms.
/// </summary>
/// <remarks>
/// <para>
/// <b>LIVE only carries a number (§5).</b> Only a <see cref="DisplayState.Live"/> window renders a
/// percent; <see cref="DisplayState.Dated"/> and <see cref="DisplayState.NA"/> render as an explicit
/// <c>n/a</c> — the "Last known" figure is popup material, never the tooltip. An n/a is NEVER omitted
/// in a way that could imply zero or safety.
/// </para>
/// <para>
/// <b>Never throws on length.</b> The result is truncated to <see cref="MaxLength"/> before it is ever
/// assigned to <c>NotifyIcon.Text</c> (the E8 budget — 127 vs the legacy 63 — is confirmed later; the
/// defensive cap stands regardless).
/// </para>
/// </remarks>
public static class TrayTooltip
{
    /// <summary>
    /// Defensive cap for <c>NotifyIcon.Text</c>. Modern .NET WinForms allows 127 chars (legacy Win32
    /// was 63); capping here means the render path can never throw on an over-long tooltip.
    /// </summary>
    public const int MaxLength = 127;

    /// <summary>
    /// Minimum characters each provider line is guaranteed before global truncation (review P2-17), so a
    /// long first line can never truncate a second provider's line away entirely.
    /// </summary>
    private const int PerProviderFloor = 40;

    private const string Separator = " · "; // " · " (U+00B7), the DESIGN.md §7 field separator.
    private const string Ellipsis = "…";

    /// <summary>Build the tooltip, truncated to <paramref name="maxLength"/> characters.</summary>
    public static string Build(UsageView view, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Providers.Count == 0)
        {
            return Truncate(AppInfo.Name, maxLength);
        }

        // Full lines first (figures + reasons + credits). If they all fit, ship them verbatim.
        var full = new List<string>(view.Providers.Count);
        foreach (var provider in view.Providers)
        {
            full.Add(BuildProviderLine(provider, includeReasons: true));
        }

        var joined = string.Join('\n', full);
        if (joined.Length <= maxLength)
        {
            return joined;
        }

        // Over budget: drop the reason texts first (least load-bearing), THEN budget each line so a second
        // provider's whole line can never be silently dropped (review P2-17 — an omission that would read as
        // implied-safe). TooltipBudget guarantees the result stays within maxLength.
        var compact = new List<string>(view.Providers.Count);
        foreach (var provider in view.Providers)
        {
            compact.Add(BuildProviderLine(provider, includeReasons: false));
        }

        return TooltipBudget.Fit(compact, maxLength, PerProviderFloor);
    }

    private static string BuildProviderLine(ProviderView provider, bool includeReasons)
    {
        var sb = new StringBuilder();
        sb.Append(ProviderLabel(provider.ProviderId)).Append(": ");

        if (provider.Windows.Count == 0)
        {
            // A disabled (opted-out) provider reads as a crisp "off" rather than "n/a (disabled)" (T32).
            if (string.Equals(provider.StatusReasonCode, "disabled", StringComparison.Ordinal))
            {
                sb.Append("off");
                return sb.ToString();
            }

            // Any other windowless source (e.g. an Unavailable one): still explicit n/a, never blank.
            sb.Append("n/a");
            if (includeReasons && !string.IsNullOrEmpty(provider.StatusReasonCode))
            {
                sb.Append(" (").Append(UsageFormat.FriendlyReason(provider.StatusReasonCode)).Append(')');
            }

            return sb.ToString();
        }

        var parts = new List<string>(provider.Windows.Count);
        foreach (var window in provider.Windows)
        {
            parts.Add(WindowFigure(window, includeReasons));
        }

        sb.Append(string.Join(Separator, parts));

        if (provider.CreditsBalance is { State: MetricState.Available, Value: { } credits })
        {
            sb.Append(Separator).Append("cr ").Append(UsageFormat.Credits(credits));
        }

        return sb.ToString();
    }

    private static string WindowFigure(WindowView window, bool includeReasons)
    {
        if (window.DisplayState == DisplayState.Live && window.Percent is { } live)
        {
            return $"{window.Label} {UsageFormat.Percent(live)}% used";
        }

        // DATED/NA both render as an explicit current-field n/a (§5). NA carries its own reason; a
        // DATED window is n/a "now" because the live reading expired — the last-known value is popup-only.
        // The reason parenthetical is the first thing dropped when the tooltip is over budget.
        var reason = window.DisplayState == DisplayState.NA ? window.ReasonCode : "no-recent-event";
        return includeReasons && !string.IsNullOrEmpty(reason)
            ? $"{window.Label} n/a ({UsageFormat.FriendlyReason(reason)})"
            : $"{window.Label} n/a";
    }

    private static string ProviderLabel(string providerId) => providerId switch
    {
        "codex" => "Codex",
        "claude" => "Claude",
        _ => Capitalise(providerId),
    };

    private static string Capitalise(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];

    private static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return maxLength == 1
            ? Ellipsis
            : string.Concat(text.AsSpan(0, maxLength - 1), Ellipsis);
    }
}
