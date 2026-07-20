namespace AIUsageTray;

/// <summary>
/// Static application metadata shared across the app and referenced by tests.
/// A get-only property (not a compile-time <c>const</c>) so a reference genuinely
/// loads this assembly at runtime — the test thereby proves the tray assembly boots.
/// </summary>
public static class AppInfo
{
    /// <summary>Product display name.</summary>
    public static string Name => "AI-Usage";
}
