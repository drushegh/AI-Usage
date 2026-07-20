namespace AIUsage.Core;

/// <summary>
/// The top-level render-ready model the UI draws (DESIGN.md §5, §7): the whole tray's state in one
/// immutable value, produced by <see cref="UsageViewBuilder.Build"/>. The icon reads
/// <see cref="OverallSeverity"/> + <see cref="Unknown"/> + <see cref="AllUnknown"/>; the popup draws
/// the <see cref="Providers"/> cards.
/// </summary>
/// <param name="OverallSeverity">
/// The worst severity across all providers/windows (DESIGN.md §7). Driven ONLY by LIVE readings and
/// the DATED monotone-floor; an n/a region contributes nothing here (it contributes to
/// <see cref="Unknown"/> instead). Defaults to <see cref="Severity.Normal"/> when nothing contributes
/// — but in that case <see cref="AllUnknown"/> is set, so the icon must not read this as "safe".
/// </param>
/// <param name="Unknown">
/// The "unknown badge": true whenever an expected provider or window is not LIVE (DESIGN.md §7
/// unknown-state propagation). This is what stops the icon ever showing an UNQUALIFIED safe state
/// while something expected is unknown — a known-safe LIVE reading alongside an n/a window is
/// <see cref="Severity.Normal"/> + <see cref="Unknown"/>, never plain green.
/// </param>
/// <param name="AllUnknown">
/// True when NOTHING contributes a real severity signal (no LIVE window anywhere and no DATED
/// monotone-floor) — the neutral distinct "?" all-unknown icon state that reads as "cannot tell",
/// never as "safe" (DESIGN.md §7). Implies <see cref="Unknown"/>.
/// </param>
/// <param name="Providers">Per-provider cards, ordered by provider id for a stable display.</param>
public sealed record UsageView(
    Severity OverallSeverity,
    bool Unknown,
    bool AllUnknown,
    IReadOnlyList<ProviderView> Providers);
