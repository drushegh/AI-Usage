using AIUsage.Core;

namespace AIUsageTray;

/// <summary>
/// The tray glyph's derived render state (VISUAL-IDENTITY.md §4.5 R1–R6, the mixed-state truth table).
/// A pure projection of <see cref="UsageView"/> that <see cref="TrayIconController"/> computes once per
/// repaint and hands to <see cref="IconRenderer"/> — kept separate from <see cref="IconRenderer"/> so the
/// truth-table logic is unit-testable without touching GDI+.
/// </summary>
/// <param name="AnyLive">
/// True iff at least one window anywhere is <see cref="DisplayState.Live"/> (rows R1–R3). False means
/// R4–R6: the arc must not be drawn at all — track only. Never a grey/desaturated "last-known" arc from
/// DATED data, never green by default before the first fetch.
/// </param>
/// <param name="Severity">
/// The worst LIVE window's severity. Meaningless when <see cref="AnyLive"/> is false (left at
/// <see cref="AIUsage.Core.Severity.Normal"/> so a careless caller can never paint a false colour from it).
/// </param>
/// <param name="Percent">
/// The percent of that SAME worst LIVE window — one metric drives both the arc's colour and its length
/// (R1: "never mixed sources"). Meaningless when <see cref="AnyLive"/> is false.
/// </param>
/// <param name="Badge">
/// True whenever any monitored provider is not fully LIVE (R2). Sourced directly from
/// <see cref="UsageView.Unknown"/>, which already excludes a deliberately-disabled ("off") provider from
/// the roll-up, so a kill-switched provider can't light the badge forever.
/// </param>
public readonly record struct TrayIconState(bool AnyLive, Severity Severity, decimal Percent, bool Badge)
{
    /// <summary>The pre-first-fetch / nothing-known state (R6) — track only, badge on, never green.</summary>
    public static readonly TrayIconState NoData = new(AnyLive: false, Severity.Normal, Percent: 0m, Badge: true);

    /// <summary>
    /// Derive the tray state from a render model (VISUAL-IDENTITY.md §4.5). Pure and side-effect-free —
    /// scans every provider's windows for the single worst LIVE reading; DATED and n/a windows never
    /// contribute to the arc, no matter their severity or magnitude.
    /// </summary>
    public static TrayIconState Compute(UsageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        WindowView? worst = null;
        foreach (var provider in view.Providers)
        {
            foreach (var window in provider.Windows)
            {
                // R1: only a LIVE window may drive the arc — a DATED monotone-floor never contributes here,
                // however high its severity (that stays in DATED grammar — tooltip/popup only, §4.4).
                if (window.DisplayState != DisplayState.Live || window.Percent is not { } percent)
                {
                    continue;
                }

                if (worst is null || percent > worst.Percent)
                {
                    worst = window;
                }
            }
        }

        return worst is null
            ? new TrayIconState(AnyLive: false, Severity.Normal, Percent: 0m, Badge: view.Unknown)
            : new TrayIconState(AnyLive: true, worst.Severity, worst.Percent!.Value, Badge: view.Unknown);
    }
}
